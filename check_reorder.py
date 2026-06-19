#!/usr/bin/env python3
"""检测 frame_id 倒退(乱序) — 对比 raw.bin(读取层) 和 lsm.csv(消费后)"""
import re, sys

def analyze(path, label):
    data = open(path, "rb").read()
    ids = []
    for line in data.split(b'\n'):
        m = re.match(rb'^(\d+),', line)
        if m:
            v = int(m.group(1))
            # 过滤明显污染值(>2^31)
            if v < 2_000_000_000:
                ids.append(v)
    back = 0
    examples = []
    for i in range(1, len(ids)):
        if ids[i] < ids[i-1]:
            back += 1
            if len(examples) < 8:
                examples.append((ids[i-1], ids[i], ids[i-1]-ids[i]))
    print(f"=== {label} ({path.split(chr(92))[-1]}) ===")
    print(f"  有效 id 数: {len(ids)}")
    print(f"  倒退次数: {back}")
    for a, b, d in examples:
        print(f"    {a} -> {b}  (倒退 {d})")
    print()

if __name__ == '__main__':
    d = sys.argv[1] if len(sys.argv) > 1 else "."
    import os
    raw = os.path.join(d, "lsm_raw.bin")
    csv = os.path.join(d, "lsm.csv")
    if os.path.exists(raw): analyze(raw, "raw.bin 读取层")
    if os.path.exists(csv): analyze(csv, "lsm.csv 消费后")
