# StarPreview — 星图离屏预览

不需要启动 ModLens 主窗口，直接把真实的 `StarGalaxyView` 渲染成 PNG。
用于快速检查星系布局、标签、聚合连线、选中关系卡，不触发主程序的页面/扫描 UI。

## 用法

```bash
cd F:\DSH-Workspace\PackDependencyTool-star

# 全图预览（使用仓库要求的 9.0.319 SDK，避免 global.json 不匹配）
"F:\DSH-Workspace\.dotnet\dotnet.exe" run --project tools/StarPreview/StarPreview.csproj -c Debug -- \
  --out screenshots/star.png

# 选中某个模组 + 放大到它（会显示右侧关系卡）
"F:\DSH-Workspace\.dotnet\dotnet.exe" run --project tools/StarPreview/StarPreview.csproj -c Debug -- \
  --out screenshots/star-selected.png --select create --focus create --zoom 1.15
```

## 参数

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--pack` | `E:\mc_ce\.minecraft\versions\SkyExplorer` | 整合包路径 |
| `--out` | `screenshots/star-preview.png` | 输出 PNG |
| `--width` / `--height` | `1600` / `900` | 画布尺寸 |
| `--select` | 无 | 选中节点 id（显示关系卡） |
| `--focus` | 无 | 相机中心节点 id |
| `--zoom` | `1.05` | 配合 `--focus` 的缩放 |
| `--camera-zoom` | 无 | 直接指定缩放（例如 `0.07`，用于检查缩小时图标/标签） |

工具只初始化 Avalonia 渲染栈和 `RenderTargetBitmap`，不创建/显示窗口；图标解码会等待最多 3 秒的 dispatcher 泵。
