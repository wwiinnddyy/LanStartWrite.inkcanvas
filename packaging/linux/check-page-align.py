#!/usr/bin/env python3
"""扫一个发布目录里的 ELF，检查机器类型与 PT_LOAD 段对齐。

用法：check-page-align.py <目录> [--require-machine NAME] [--require-align N]
退出码：0 全通过，1 有违规（或一个 ELF 都没扫到）。

为什么不用 readelf：这台开发机上根本没有它（Git Bash 不带 binutils），而这份检查要先在本机
对交叉发布的产物跑一遍、再在 CI 上跑同一份 —— 两套环境里唯一保证在着的是 Python。

为什么段对齐值得一条门禁：arm64 内核可以按 4K / 16K / 64K 页编译（Android 15+ 与发行版的
"16k pages" 变体已经以 16K 为常态），而 PT_LOAD 的 p_align 若还停在 0x1000，包在小页内核的
机器上**装得上、跑不起来** —— 报的是 exec 时 "cannot map segment"（或被 dlopen 拒掉），
现场指不到"对齐"这件事上。症状延迟到用户机器上，所以必须在 CI 里判。

x86_64 那边仍按 4K 要求：主流内核就是 4K，把门槛抬到 16K 只会得到一条没人会去满足的红。
"""

import argparse
import os
import struct
import sys

MACHINES = {
    3: "i386",
    40: "arm",
    62: "x86_64",
    183: "aarch64",
    243: "riscv",
}

PT_LOAD = 1
PAGE_SIZE = 0


def read_elf(path):
    """返回 (machine, 最小 PT_LOAD p_align)；p_align 为 None 表示没有 PT_LOAD。

    不是 ELF（magic 对不上）返回 None，交由调用方计入"跳过"。
    """
    with open(path, "rb") as f:
        head = f.read(64)
        if len(head) < 64 or head[:4] != b"\x7fELF":
            return None
        is64 = head[4] == 2
        little = head[5] == 1
        endian = "<" if little else ">"
        f.seek(16)
        if is64:
            e_type, e_machine = struct.unpack(endian + "HH", f.read(4))
            f.seek(0, os.SEEK_END)
            f.seek(32)
            (e_phoff,) = struct.unpack(endian + "Q", f.read(8))
            f.seek(54)
            e_phentsize, e_phnum = struct.unpack(endian + "HH", f.read(4))
        else:
            e_type, e_machine = struct.unpack(endian + "HH", f.read(4))
            f.seek(28)
            (e_phoff,) = struct.unpack(endian + "I", f.read(4))
            f.seek(42)
            e_phentsize, e_phnum = struct.unpack(endian + "HH", f.read(4))

        aligns = []
        for i in range(e_phnum):
            f.seek(e_phoff + i * e_phentsize)
            ph = f.read(e_phentsize)
            if len(ph) < (56 if is64 else 32):
                break
            p_type = struct.unpack(endian + "I", ph[0:4])[0]
            if p_type != PT_LOAD:
                continue
            # ELF64：p_align 在第 48 字节起（8 字节）；ELF32：第 28 字节起（4 字节）。
            if is64:
                aligns.append(struct.unpack(endian + "Q", ph[48:56])[0])
            else:
                aligns.append(struct.unpack(endian + "I", ph[28:32])[0])

    return e_machine, (min(aligns) if aligns else None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("target", help="要扫的目录（发布目录 / AppDir 根）")
    ap.add_argument("--require-machine", default=None,
                    help="要求的 ELF 机器名（x86_64 / aarch64），不填则只报分布")
    ap.add_argument("--require-align", type=lambda s: int(s, 0), default=PAGE_SIZE,
                    help="PT_LOAD p_align 的下限，接受 0x 前缀（如 0x4000）")
    args = ap.parse_args()

    # 本机是 Windows 时标准流跟着控制台代码页走，中文输出会乱码甚至抛 UnicodeEncodeError ——
    # 检查工具自己崩掉是最坏的一种"红"，它看着像产物有问题。
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    if args.require_align and args.require_align & (args.require_align - 1):
        print("::error--require-align 必须是 2 的幂（p_align 的定义就是 2 的幂）")
        return 1

    files = []
    for root, _dirs, names in os.walk(args.target):
        for n in names:
            files.append(os.path.join(root, n))
    files.sort()

    scanned = 0
    skipped = 0          # 非 ELF 与读不动的：数据文件、托管 dll、脚本都在这一类
    no_load = 0          # 是 ELF 但没有 PT_LOAD（.o 一类的可重定位目标）
    by_machine = {}
    min_align = None
    bad = []

    for path in files:
        try:
            info = read_elf(path)
        except Exception as exc:  # 半截文件 / 权限：如实报，不算通过
            bad.append((path, f"读失败：{exc}"))
            continue
        if info is None:
            skipped += 1
            continue
        machine, align = info
        scanned += 1
        name = MACHINES.get(machine, f"#{machine}")
        by_machine[name] = by_machine.get(name, 0) + 1
        if args.require_machine and name != args.require_machine:
            bad.append((path, f"机器类型是 {name}，这台要求 {args.require_machine}"))
        if align is None:
            no_load += 1
            continue
        if min_align is None or align < min_align:
            min_align = align
        if args.require_align and align < args.require_align:
            bad.append((path, f"PT_LOAD p_align = 0x{align:x}，要求 ≥ 0x{args.require_align:x}"))

    dist = " / ".join(f"{k} {v}" for k, v in sorted(by_machine.items())) or "无"
    print(f"== ELF 门禁：{args.target} ==")
    print(f"  扫到 ELF {scanned} 个（{dist}），非 ELF 跳过 {skipped} 个，无 PT_LOAD {no_load} 个")
    if min_align is not None:
        print(f"  最小 PT_LOAD p_align = 0x{min_align:x}（{min_align} 字节）"
              + (f"，要求 ≥ 0x{args.require_align:x}" if args.require_align else "（未设下限）"))

    # 一个 ELF 都没扫到必须红：绿在这里等于"什么都没检查"。
    # 典型成因是目录写错或产物没落位 —— 那正是"检查在但没接上"的形状。
    if scanned == 0:
        print(f"::error 在 {args.target} 下一个 ELF 都没扫到 —— 目录错还是产物没编出来？")
        return 1
    if args.require_machine and by_machine.get(args.require_machine, 0) != scanned:
        print(f"::error 有 ELF 不是 {args.require_machine}：{dist}")
        return 1
    if bad:
        for path, why in bad[:20]:
            print(f"::error {path}：{why}")
        if len(bad) > 20:
            print(f"  ……另有 {len(bad) - 20} 条")
        print(f"共 {len(bad)} 条违规")
        return 1
    print("  OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
