# ATAS Forced Risk Manager

## Overview / 概览

English: ATAS Forced Risk Manager is an ATAS Chart Strategy that monitors session loss and consecutive losing updates. When a configured risk limit is reached, it pauses the chart with a fullscreen warning, cancels working orders for the current account, attempts to close the current position, and can request a Windows shutdown for a cooldown break.

中文：ATAS Forced Risk Manager 是用于 ATAS 的 Chart Strategy，用来监控本次策略启动后的亏损和连续亏损次数。当触发已设置的风险限制时，它会显示全屏风险提示，撤销当前账户的挂单，尝试平掉当前持仓，并可选择请求 Windows 关机作为冷静期保护。

## Features / 功能

English:

- Tracks session loss using ClosedPnL plus OpenPnL delta from the strategy start baseline.
- Tracks consecutive losing ClosedPnL updates.
- Cancels active orders for the current account after a risk trigger.
- Attempts to close the current position after a risk trigger.
- Can request Windows shutdown after the risk response when enabled.
- Silently checks TradingHub latest-version metadata and shows one small update line only when a newer version exists.

中文：

- 使用策略启动时的基准，按 ClosedPnL + OpenPnL 的变化计算本次会话亏损。
- 统计 ClosedPnL 连续亏损更新次数。
- 触发风险限制后撤销当前账户的活动挂单。
- 触发风险限制后尝试平掉当前持仓。
- 开启后可在风险响应完成后请求 Windows 关机。
- 静默检查 TradingHub 最新版本元数据；只有发现新版本时才显示一行小号更新提示。

## Parameters / 参数

| Parameter | Default | English | 中文 |
| --- | ---: | --- | --- |
| `Max Loss Amount` | `500` | Maximum session loss before the risk response triggers. | 触发风险响应的本次会话最大亏损。 |
| `Max Consecutive Losses` | `3` | Maximum consecutive losing ClosedPnL updates before triggering. | 触发风险响应的连续亏损更新次数。 |
| `Enable Windows Shutdown` | `true` | Requests Windows shutdown after the risk response. | 风险响应后请求 Windows 关机。 |

## Risk Notice / 风险提示

English: This tool can cancel orders, close positions, and request operating-system shutdown. Test it in simulation first, confirm account selection in ATAS, and do not rely on it as the only risk-control layer.

中文：本工具可以撤单、平仓并请求操作系统关机。请先在模拟环境测试，确认 ATAS 中的账户选择，不要把它作为唯一风险控制层。

## Build / 构建

English: Run from this folder:

中文：在本目录运行：

```powershell
dotnet build .\AtasForcedRiskManagementPlugin.csproj -c Release
```

Expected artifact / 预期产物：

```text
build\AtasForcedRiskManagementPlugin.dll
```

## Install / 安装

English: Copy the official DLL to the ATAS strategies folder:

中文：将正式 DLL 复制到 ATAS 策略目录：

```text
%APPDATA%\ATAS\Strategies
```

English: Restart ATAS, then search for:

中文：重新启动 ATAS 后搜索：

```text
ATAS Forced Risk Manager
```
