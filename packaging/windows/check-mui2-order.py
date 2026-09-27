#!/usr/bin/env python3
"""检查 installer.nsi 里 MUI2 的三处顺序契约，不调 makensis。

用法：check-mui2-order.py [脚本路径]   （默认 packaging/windows/installer.nsi）
退出码：0 全通过，1 有违规。

为什么需要它：这条门禁的全部价值在于**把一次十分钟的 CI 变成一次一秒的本地检查**。
本机装不上 NSIS，所以改完脚本只能靠 CI 验，而 makensis 报出来的是
`Error in script "installer.nsi" on line 98 -- aborting creation process`
—— **只有行号，没有任何解释**。真正的线索是它前面那六条 warning
（`MUI_LANGUAGE[EX] should be inserted after the MUI_[UN]PAGE_* macros` 等），
而 run 36256606331 就是卡在这里查的。

检查三件事，各自对应 MUI2 的真实实现：

1. **`MUI_PAGE_*` / `MUI_UNPAGE_*` 全在第一个 `MUI_LANGUAGE` 之前。**
   MUI2 的 `MUI_PAGE_INIT` 宏在展开时读"到此为止已经声明了哪些页面"，
   所以语言块插在页面中间，它是对着半套页面生成向导的。反过来同样错。

2. **一种语言只有一个 `MUI_LANGUAGE` 块。**
   `MUI_LANGUAGE` 内部把语言压进一个 NSIS 栈，`MUI_UNLANGUAGE` 是后进先出地弹。
   同一个键名写两遍之后，弹出来的与要的不一致，`!error` 里**一个字都不会说**。

3. **`MUI_UNLANGUAGE` 一律不许出现。**
   卸载器的键名以 `MUI_UN` 开头（`MUI_UNCONFIRMPAGE_*`），MUI2 按前缀自己认；
   卸载器的 `LangString` 写在同一个 `MUI_LANGUAGE` 块里即可。
   `MUI_UNLANGUAGE` 只在"想让卸载器拿不到某些语言"时才用，本项目不需要。
"""

import argparse
import re
import sys

PAGE_RE = re.compile(r"^!\s*insertmacro\s+MUI_(?:UN)?PAGE_(\w+)", re.IGNORECASE)
LANG_RE = re.compile(r"^!\s*insertmacro\s+MUI_LANGUAGE\s+(\S+)", re.IGNORECASE)
UNLANG_RE = re.compile(r"^!\s*insertmacro\s+MUI_UNLANGUAGE\b", re.IGNORECASE)


def strip_comment(line):
    """去掉分号注释。NSIS 的 ';' 只在行首才是注释，但脚本里没人写行尾 ';'，
    而按行首判定会把 ';' 开头的注释留着当成代码 —— 宁可多看一眼。"""
    stripped = line.lstrip()
    if stripped.startswith(";"):
        return ""
    return line


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("script", nargs="?",
                    default="packaging/windows/installer.nsi", help="要查的 .nsi 脚本")
    args = ap.parse_args()

    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    try:
        # utf-8-sig：这个文件必须带 BOM（没有 BOM 时 makensis 按系统代码页读，
        # 英文机器上界面里的中文会变乱码），所以读的时候把 BOM 当掉。
        with open(args.script, "r", encoding="utf-8-sig") as f:
            lines = f.read().splitlines()
    except OSError as exc:
        print(f"::error 读不了 {args.script}：{exc}")
        return 1

    print(f"== MUI2 顺序门禁：{args.script} ==")

    pages = []      # (行号, 宏名)
    langs = []      # (行号, 语言)
    unlangs = []    # 行号
    for n, raw in enumerate(lines, start=1):
        line = strip_comment(raw)
        m = PAGE_RE.match(line)
        if m:
            pages.append((n, m.group(1).upper()))
            continue
        m = LANG_RE.match(line)
        if m:
            langs.append((n, m.group(1).strip('"\'')))
            continue
        if UNLANG_RE.match(line):
            unlangs.append(n)

    print(f"  页面 {len(pages)} 条，语言块 {len(langs)} 条，MUI_UNLANGUAGE {len(unlangs)} 条")
    if not pages:
        print("::error 脚本里一条 MUI_PAGE_* 都没有 —— 目录名写错了？")
        return 1
    if not langs:
        print("::error 脚本里一条 MUI_LANGUAGE 都没有 —— 界面会落到内置英文")
        return 1

    bad = []

    first_lang = langs[0][0]
    for n, name in pages:
        if n > first_lang:
            bad.append(f"第 {n} 行 MUI_PAGE_{name} 在第一个 MUI_LANGUAGE（第 {first_lang} 行）之后"
                       f" —— 页面必须全部写在语言块前面")

    seen = {}
    for n, lang in langs:
        if lang in seen:
            bad.append(f"第 {n} 行 MUI_LANGUAGE \"{lang}\" 是第二次出现"
                       f"（第一次在第 {seen[lang]} 行）—— 每种语言只能有一个块")
        else:
            seen[lang] = n

    for n in unlangs:
        bad.append(f"第 {n} 行 MUI_UNLANGUAGE 不该出现 —— 卸载器的键名以 MUI_UN 开头，"
                   f"写在同一个 MUI_LANGUAGE 块里即可")

    if bad:
        for why in bad:
            print(f"::error {args.script}：{why}")
        print(f"共 {len(bad)} 条违规")
        return 1

    print("  语言块顺序：" + " → ".join(f"第{n}行 {l}" for n, l in langs))
    print("  OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
