"""最小 OpenAI 兼容 LLM 客户端（urllib 标准库，超时 + 1s/4s/16s 退避重试）。

对应文档：04 §1.1（端点与参数）、§1.3（参数陷阱）、§6.3/§6.4（请求体与重试）、§13（密钥管理）。

安全红线（04 §13 / 03 D21）
  * key 只从环境变量或 .env.local 读，**绝不**写进代码、日志、报告、git。
  * 任何要打印/落盘的字符串都先过 redact()，把 key 打码成 sk-***。
  * 异常里也不许带 key：本模块构造的错误消息一律经过 redact。

实测坑（04 §1.3 / task-11 任务书）
  * max_tokens **不被强制执行**，不能靠它控截断 —— 只能靠批大小 + finish_reason。
  * 模型可能把 JSON 包进三反引号围栏 —— 剥壳是 translate.py 的事，这里只负责原样返回 content。
"""

from __future__ import annotations

import json
import os
import re
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

# ---------------------------------------------------------------------------
# 常量
# ---------------------------------------------------------------------------

ENV_BASE = "UI18N_LLM_BASE"
ENV_KEY = "UI18N_LLM_KEY"
ENV_MODEL = "UI18N_LLM_MODEL"
ENV_REASONING = "UI18N_LLM_REASONING"

#: 仓库根（tools/i18n_tools/llm.py → 上三级）
REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_ENV_FILE = REPO_ROOT / ".env.local"

#: 退避序列（04 §6.4：失败退避重试 3 次 1s / 4s / 16s）
BACKOFFS = (1.0, 4.0, 16.0)
DEFAULT_TIMEOUT = 180.0
DEFAULT_TEMPERATURE = 0.2

#: 打码后的样子
MASKED = "sk-***"


class LlmConfigError(RuntimeError):
    """配置缺失/非法（key 不在、base 不在）。消息里绝不出现密钥。"""


class LlmError(RuntimeError):
    """单次调用失败（已用完重试）。"""


# ---------------------------------------------------------------------------
# .env 解析（约 20 行，不装 python-dotenv）
# ---------------------------------------------------------------------------

def parse_env_file(path) -> dict:
    """极简 .env 解析：KEY=VALUE，忽略 # 注释与空行，剥一层引号，容忍 export 前缀。"""
    out = {}
    try:
        text = Path(path).read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError):
        return out

    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if key.startswith("export "):
            key = key[len("export "):].strip()
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        if key:
            out[key] = value
    return out


def redact(text, *secrets) -> str:
    """把密钥（以及 Authorization 头）替换成 sk-***。任何日志/报告/异常都要过这一道。"""
    if text is None:
        return ""
    text = str(text)
    for secret in secrets:
        if secret and len(secret) >= 4:
            text = text.replace(secret, MASKED)
    # 兜底：任何 Bearer <token> 形式（含 .env 换了新 key 而本进程没拿到的情形）
    return re.sub(r"(Bearer\s+)\S+", lambda match: match.group(1) + MASKED, text)


# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------

@dataclass
class LlmConfig:
    """端点配置。repr 里 key 不出场，防止被随手 print 进日志。"""

    base: str
    key: str = field(repr=False)
    model: str
    reasoning: str = "none"
    timeout: float = DEFAULT_TIMEOUT
    env_file: str = ""

    @property
    def url(self) -> str:
        return self.base.rstrip("/") + "/chat/completions"

    def describe(self) -> str:
        """人读的一行配置摘要（无密钥）。"""
        return "model={} base={} reasoning={} timeout={}s".format(
            self.model, self.base, self.reasoning or "(default)", int(self.timeout)
        )

    def __repr__(self) -> str:  # pragma: no cover - 防手滑
        return "LlmConfig({})".format(self.describe())


def load_config(env_file=None, timeout: float = DEFAULT_TIMEOUT) -> LlmConfig:
    """读环境变量 + .env.local（环境变量优先，.env.local 只补缺失的）。

    密钥缺失时抛 LlmConfigError，消息里只说"缺哪个名字"，绝不回显值。
    """
    path = Path(env_file) if env_file else DEFAULT_ENV_FILE
    file_values = parse_env_file(path)

    def pick(name: str) -> str:
        return (os.environ.get(name) or file_values.get(name) or "").strip()

    base = pick(ENV_BASE)
    key = pick(ENV_KEY)
    model = pick(ENV_MODEL)
    reasoning = pick(ENV_REASONING)

    missing = [name for name, value in ((ENV_BASE, base), (ENV_KEY, key), (ENV_MODEL, model)) if not value]
    if missing:
        raise LlmConfigError(
            "LLM 配置缺失：{}（应写在 {} 或环境变量里；文件名/内容均不入库）".format(
                ", ".join(missing), path
            )
        )

    return LlmConfig(base=base, key=key, model=model, reasoning=reasoning,
                     timeout=timeout, env_file=str(path))


# ---------------------------------------------------------------------------
# 客户端
# ---------------------------------------------------------------------------

@dataclass
class ChatResult:
    """一次成功调用的结果。"""

    content: str
    finish_reason: str
    usage: dict
    elapsed: float
    attempts: int


class LlmClient:
    """无共享可变状态的重试计数之外没有跨线程耦合；每线程一个实例或共享一个都安全。"""

    def __init__(self, config: LlmConfig, sleep=time.sleep, log=None):
        self.config = config
        self._sleep = sleep
        self._log = log or (lambda message: None)
        # 累计用量（跨线程累加；只用于报告估算）
        self.tokens = {"prompt": 0, "completion": 0, "reasoning": 0, "requests": 0, "failures": 0}

    # -- 内部 ------------------------------------------------------------

    def _post(self, body: dict) -> dict:
        """一次 POST，不重试。返回解析后的 JSON 对象。"""
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            self.config.url,
            data=data,
            headers={
                "Authorization": "Bearer " + self.config.key,
                "Content-Type": "application/json; charset=utf-8",
                "Accept": "application/json",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.config.timeout) as response:
                raw = response.read()
        except urllib.error.HTTPError as exc:
            detail = ""
            try:
                detail = exc.read().decode("utf-8", "replace")[:400]
            except Exception:  # noqa: BLE001 - 读错误体失败不该掩盖原错误
                detail = ""
            raise LlmError(redact("HTTP {} {}".format(exc.code, detail), self.config.key)) from None
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise LlmError(redact("网络错误 {}: {}".format(type(exc).__name__, exc),
                                  self.config.key)) from None

        try:
            return json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            head = raw[:200].decode("utf-8", "replace")
            raise LlmError(redact("响应不是合法 JSON（前 200 字节）：" + head, self.config.key)) from None

    @staticmethod
    def _usage_of(payload: dict) -> dict:
        usage = payload.get("usage") or {}
        details = usage.get("completion_tokens_details") or {}
        return {
            "prompt": int(usage.get("prompt_tokens") or 0),
            "completion": int(usage.get("completion_tokens") or 0),
            "reasoning": int(details.get("reasoning_tokens") or 0),
            "total": int(usage.get("total_tokens") or 0),
        }

    @staticmethod
    def _retryable(message: str) -> bool:
        """4xx（除 429）是配置/请求问题，重试没有意义。"""
        match = re.search(r"HTTP (\d{3})", message)
        if not match:
            return True                     # 网络类错误一律可重试
        code = int(match.group(1))
        return code == 429 or code >= 500

    # -- 对外 ------------------------------------------------------------

    def chat(self, messages, temperature: float = DEFAULT_TEMPERATURE,
             max_tokens=None) -> ChatResult:
        """发一次对话补全，带 1s/4s/16s 退避重试（共 4 次机会）。"""
        body = {
            "model": self.config.model,
            "temperature": temperature,
            "messages": messages,
            # response_format 被接受但无可测收益（04 §1.3）：留着当保险，不依赖它
            "response_format": {"type": "json_object"},
        }
        if self.config.reasoning:
            body["reasoning_effort"] = self.config.reasoning
        if max_tokens:
            # 04 §1.3：max_tokens 不被强制执行，传了也只是写个意愿
            body["max_tokens"] = int(max_tokens)

        started = time.time()
        attempts = 0
        last_error = ""

        for attempt in range(len(BACKOFFS) + 1):
            attempts = attempt + 1
            try:
                payload = self._post(body)
            except LlmError as exc:
                last_error = str(exc)
                if not self._retryable(last_error) or attempt == len(BACKOFFS):
                    self.tokens["failures"] += 1
                    raise
                delay = BACKOFFS[attempt]
                self._log("    重试 {}/{} 退避 {}s：{}".format(
                    attempt + 1, len(BACKOFFS), delay, last_error[:160]))
                self._sleep(delay)
                continue

            choices = payload.get("choices") or []
            if not choices:
                last_error = "响应没有 choices"
                if attempt == len(BACKOFFS):
                    self.tokens["failures"] += 1
                    raise LlmError(redact(last_error, self.config.key))
                self._sleep(BACKOFFS[attempt])
                continue

            choice = choices[0]
            message = choice.get("message") or {}
            content = message.get("content") or ""
            usage = self._usage_of(payload)
            self.tokens["prompt"] += usage["prompt"]
            self.tokens["completion"] += usage["completion"]
            self.tokens["reasoning"] += usage["reasoning"]
            self.tokens["requests"] += 1

            return ChatResult(
                content=content,
                finish_reason=str(choice.get("finish_reason") or ""),
                usage=usage,
                elapsed=time.time() - started,
                attempts=attempts,
            )

        raise LlmError(redact("重试耗尽：" + last_error, self.config.key))

    def check(self) -> dict:
        """连通性自检：问一条定长翻译，返回耗时/模型/finish_reason（不含密钥）。"""
        result = self.chat([
            {"role": "system", "content": "只输出 JSON：{\"items\":[{\"id\":1,\"zh\":\"<译文>\"}]}"},
            {"role": "user", "content": json.dumps(
                {"batch": [{"id": 1, "t": "Switch to Hangar", "role": "button", "mod": "UI18n"}]},
                ensure_ascii=False)},
        ])
        return {
            "ok": bool(result.content.strip()),
            "model": self.config.model,
            "finish_reason": result.finish_reason,
            "elapsed_s": round(result.elapsed, 2),
            "attempts": result.attempts,
            "usage": result.usage,
            "content_head": result.content[:120],
        }


# ---------------------------------------------------------------------------
# 命令行：python -m i18n_tools.llm --check
# ---------------------------------------------------------------------------

def main(argv=None) -> int:
    import argparse

    from . import ensure_stdout

    ensure_stdout()
    parser = argparse.ArgumentParser(prog="i18n_tools.llm", description="LLM 端点自检（不打印密钥）")
    parser.add_argument("--check", action="store_true", help="发一次最小请求验通")
    parser.add_argument("--env-file", default=None, help="默认仓库根 .env.local")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT)
    args = parser.parse_args(argv)

    try:
        config = load_config(args.env_file, timeout=args.timeout)
    except LlmConfigError as exc:
        print("[FATAL]", exc)
        return 2

    print("[配置]", config.describe())
    if not args.check:
        return 0

    client = LlmClient(config)
    try:
        info = client.check()
    except LlmError as exc:
        print("[FAIL]", redact(str(exc), config.key))
        return 1

    print("[OK] 用时 {elapsed_s}s attempts={attempts} finish={finish_reason} usage={usage}".format(**info))
    print("[OK] 返回:", info["content_head"].replace("\n", " "))
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
