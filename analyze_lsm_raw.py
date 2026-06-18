#!/usr/bin/env python3
"""
分析 lsm_raw.bin — 检测帧丢失、乱序、断行
"""
import sys
from pathlib import Path

def analyze_lsm_raw(path):
    """解析原始字节流中的CSV行，统计帧ID连续性"""
    data = Path(path).read_bytes()

    # 按 \n 分割成行
    lines = data.split(b'\n')

    print(f"总字节数: {len(data):,}")
    print(f"总行数: {len(lines):,}")
    print()

    frame_ids = []
    torn_lines = []

    for i, line in enumerate(lines):
        line = line.strip()
        if not line:
            continue

        # 跳过CSV头
        if line.startswith(b'frame_id,'):
            continue

        # 尝试解析帧ID（第一列）
        try:
            parts = line.split(b',')
            if len(parts) < 8:
                # 行不完整（列数不够，LSM应该有8列）
                torn_lines.append((i+1, line[:80]))  # 记录行号和前80字节
                continue

            frame_id = int(parts[0])
            frame_ids.append(frame_id)
        except (ValueError, IndexError):
            # 无法解析的行
            torn_lines.append((i+1, line[:80]))

    print(f"有效帧数: {len(frame_ids):,}")
    print(f"断行/无效行: {len(torn_lines):,}")
    print()

    if torn_lines:
        print("前10个断行样本:")
        for line_no, content in torn_lines[:10]:
            try:
                preview = content.decode('utf-8', errors='replace')
            except:
                preview = str(content)
            print(f"  行{line_no}: {preview}")
        print()

    if len(frame_ids) < 2:
        print("帧数不足，无法分析连续性")
        return

    # 分析帧ID连续性
    expected = frame_ids[0]
    gaps = []
    reorders = []

    for i, fid in enumerate(frame_ids):
        if fid != expected:
            if fid > expected:
                # 跳帧（丢失）
                gaps.append((expected, fid - 1, fid - expected))
                expected = fid + 1
            else:
                # 乱序（fid < expected）
                reorders.append((i, fid, expected))
                expected = expected + 1  # 继续往前
        else:
            expected = fid + 1

    # 统计总帧数（理论上应该收到的）
    first_id = frame_ids[0]
    last_id = frame_ids[-1]
    expected_count = last_id - first_id + 1
    actual_count = len(frame_ids)
    loss_count = expected_count - actual_count
    loss_rate = (loss_count / expected_count * 100) if expected_count > 0 else 0

    print(f"帧ID范围: {first_id} → {last_id}")
    print(f"理论帧数: {expected_count:,}")
    print(f"实际帧数: {actual_count:,}")
    print(f"丢失帧数: {loss_count:,}")
    print(f"丢帧率: {loss_rate:.2f}%")
    print()

    if gaps:
        print(f"发现 {len(gaps)} 个跳帧区间:")
        for start, end, count in gaps[:20]:  # 显示前20个
            print(f"  {start}→{end} (丢失 {count} 帧)")
        if len(gaps) > 20:
            print(f"  ... 还有 {len(gaps)-20} 个区间")
        print()

    if reorders:
        print(f"发现 {len(reorders)} 个乱序:")
        for idx, fid, exp in reorders[:20]:
            print(f"  索引{idx}: 收到{fid}, 期望{exp}")
        if len(reorders) > 20:
            print(f"  ... 还有 {len(reorders)-20} 个")
        print()

    # 结论
    print("=" * 60)
    if loss_rate < 0.1 and len(torn_lines) == 0:
        print("✅ 优秀: 几乎无丢帧，无断行")
    elif loss_rate < 1.0:
        print("⚠️  可接受: 丢帧率 < 1%")
    else:
        print("❌ 严重: 丢帧率过高")
    print("=" * 60)

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("用法: python analyze_lsm_raw.py <lsm_raw.bin路径>")
        sys.exit(1)

    path = sys.argv[1]
    if not Path(path).exists():
        print(f"文件不存在: {path}")
        sys.exit(1)

    analyze_lsm_raw(path)
