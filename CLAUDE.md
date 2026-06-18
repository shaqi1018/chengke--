# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**VibrationMonitor** — a WPF desktop host application (.NET 6) for an STM32U575 three-sensor vibration acquisition device. Communicates via USB CDC virtual serial port. Displays real-time waveforms, controls acquisition, and configures sensors.

## Build & Run

```bash
# Build (from project root)
dotnet build VibrationMonitor/VibrationMonitor.csproj

# Build Release (win-x86, self-contained)
dotnet publish VibrationMonitor/VibrationMonitor.csproj -c Release -r win-x86 --self-contained

# Run
dotnet run --project VibrationMonitor/VibrationMonitor.csproj
```

No test suite exists in this repository.

## Architecture

**MVVM pattern** with data binding. All source lives under `VibrationMonitor/`.

### Data Flow

```
Device (STM32 USB CDC)
  → SerialPortManager (background read thread, byte-by-byte line parsing)
    → ClassifyLine() splits into: CommandResponse | CsvData | CsvHeader | Unknown
      → DeviceCommander consumes CommandResponse lines (request/response with ManualResetEventSlim)
      → MainViewModel.OnLineReceived consumes CsvData lines → SensorFrame → _frameBuffer → OxyPlot charts
```

### Key Classes

| Layer | File | Role |
|-------|------|------|
| **Transport** | `Services/SerialPortManager.cs` | Serial port open/close/read/write. Background thread reads bytes, assembles lines, classifies them, fires events. STM32 auto-detection via VID `0483` / PID `5740`. |
| **Protocol** | `Services/DeviceCommander.cs` | Wraps SerialPortManager. `SendAndWait()` blocks until a CommandResponse line arrives (or timeout). Methods: `Ping`, `Status`, `AcqStart`, `AcqStop`, `SetSensorParam`, `Msc`. |
| **Parsing** | `Services/DataParser.cs` | Static. `ParseCsvLine()` → `SensorFrame`. `ParseStatusResponse()` → `DeviceStatus` (parses key-value lines like `tick=12345 heap=45678`). |
| **Models** | `Models/SensorData.cs` | Immutable records: `SensorFrame`, `SensorConfig`, `AcqState`, `DeviceStatus`. |
| **Models** | `Models/LineType.cs` | Enum + `ClassifiedLine` record. |
| **ViewModel** | `ViewModels/MainViewModel.cs` | Owns all UI state, commands, timers. Status polling every 2s via `DispatcherTimer`. Chart refresh at 30fps. CSV recording to `Recordings/` folder. Contains inline `RelayCommand` implementation. |
| **View** | `MainWindow.xaml` + `.cs` | Two-column layout: left sidebar (connection, acquisition, sensor config, MSC mode) + right area (status bar, OxyPlot tabs, console log). |

### Command/Data Separation

USB CDC shares one interface for commands and streaming data. Classification rules in `SerialPortManager.ClassifyLine()`:
- **CsvHeader**: starts with `frame_id,`
- **CsvData**: starts with digit or `-`
- **CommandResponse**: starts with `OK`, `ERR`, `ACQ`, `tick=`, `flow `, `sensor `, `frame `, `mode `, `acq `, `cfg `, `pong`, `Commands`
- **Unknown**: everything else

### Sensors

| ID | Chip | Data in stream |
|----|------|----------------|
| `lsm` | LSM6DSOX | 3-axis accel (mg) + 3-axis gyro (mdps) + temperature (°C) |
| `h3` | H3LIS100DL | 3-axis high-g accel (mg) + raw int8 |
| `qma` | QMA6100P | 3-axis accel (mg) + raw int14 |

### Protocol Details

All commands are ASCII text terminated by `\r\n`. See `HOST_APP_PROMPT.md` for the full command reference, CSV column format, SD card file structure, and data conversion formulas.

### Design System (XAML)

Defined as `Window.Resources` in `MainWindow.xaml`:
- Background hierarchy: `#f0f2f5` < `#f8f9fc` < `#ffffff`
- Primary: `#00796b` (teal). Status: green `#2e7d32`, red `#c62828`, orange `#e65100`, blue `#1565c0`
- Chart axis colors: X=red `D32F2F`, Y=green `388E3C`, Z=blue `1976D2`
- Style keys: `Btn`, `BtnPrimary`, `BtnSuccess`, `BtnDanger`, `BtnSm`, `Tb`, `Cb`, `Lbl`, `Card`, `CardTitle`, `LogBox`
