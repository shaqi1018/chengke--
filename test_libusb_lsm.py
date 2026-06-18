#!/usr/bin/env python3
"""
终极测试:用 libusb(pyusb) 直接读 LSM,证明 USB FS 能跑满 6664Hz。

前置:
1. pip install pyusb
2. Zadig 把设备(0483:5721)驱动切到 libusbK
3. 固件发 acq_start usb 0 开始采集

运行:python test_libusb_lsm.py
预期:20 秒收到 ~133k 行(6664Hz),证明 USB FS 够、WinUSB 慢。
"""
import usb.core
import usb.util
import time
import sys

VID = 0x0483
PID = 0x5721
EP_LSM_IN = 0x81

def main():
    # 查找设备
    dev = usb.core.find(idVendor=VID, idProduct=PID)
    if dev is None:
        print(f"错误:未找到设备 {VID:04X}:{PID:04X}")
        print("请确认:")
        print("  1. 设备已插入")
        print("  2. Zadig 已将驱动切到 libusbK")
        sys.exit(1)

    # Claim interface 0
    if dev.is_kernel_driver_active(0):
        dev.detach_kernel_driver(0)
    dev.set_configuration()
    usb.util.claim_interface(dev, 0)

    print(f"已连接 {VID:04X}:{PID:04X}")
    print("开始读取 LSM(0x81)...")
    print("请在固件发: acq_start usb 0")
    print("按 Ctrl+C 停止\n")

    buf = bytearray()
    lines = 0
    bytes_total = 0
    start = time.time()
    last_report = start

    try:
        while True:
            try:
                # 读取 EP1(LSM),64KB 缓冲,1000ms 超时
                chunk = dev.read(EP_LSM_IN, 65536, timeout=1000)
                if chunk:
                    buf.extend(chunk)
                    bytes_total += len(chunk)

                    # 按行切分(与上位机逻辑一致)
                    while True:
                        try:
                            idx = buf.index(b'\n')
                        except ValueError:
                            break

                        line = buf[:idx].decode('ascii', errors='ignore').strip()
                        buf = buf[idx+1:]

                        if line:
                            lines += 1

                    # 每秒报告
                    now = time.time()
                    if now - last_report >= 1.0:
                        elapsed = now - start
                        rate = lines / elapsed if elapsed > 0 else 0
                        bandwidth = bytes_total / elapsed / 1024 if elapsed > 0 else 0
                        print(f"[{elapsed:6.1f}s] 收到 {lines:7d} 行 | 速率 {rate:7.1f} Hz | 带宽 {bandwidth:6.1f} KB/s")
                        last_report = now

            except usb.core.USBError as e:
                if e.errno == 110:  # Timeout
                    continue
                else:
                    raise

    except KeyboardInterrupt:
        print("\n停止")
    finally:
        elapsed = time.time() - start
        if elapsed > 0:
            avg_rate = lines / elapsed
            avg_bw = bytes_total / elapsed / 1024
            print(f"\n总计:")
            print(f"  时长:   {elapsed:.1f} 秒")
            print(f"  收到:   {lines} 行")
            print(f"  平均:   {avg_rate:.1f} Hz (理论 6664 Hz)")
            print(f"  带宽:   {avg_bw:.1f} KB/s (理论 ~386 KB/s)")
            print(f"  完成率: {avg_rate/6664*100:.1f}%")

            if avg_rate > 6000:
                print("\n✅ USB FS 能跑满!WinUSB 是瓶颈,不是 USB 带宽。")
            else:
                print("\n⚠️  未达理论速率,可能:")
                print("  1. 固件未发 acq_start usb")
                print("  2. libusb 安装/驱动有问题")

        usb.util.release_interface(dev, 0)
        usb.util.dispose_resources(dev)

if __name__ == '__main__':
    main()
