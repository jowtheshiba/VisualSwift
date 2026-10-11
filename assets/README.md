# VisualSwift icons

Three original raster assets were created with the built-in image_gen tool on 2026-10-11. The exact prompts are recorded in generation.json.

| Asset | Purpose |
| --- | --- |
| generated/visualswift.png | Extension emblem combining an orange bird and purple developer ribbon |
| generated/swift-console.png | Console Application template, terminal with an orange bird |
| generated/swift-library.png | Library template, purple package with an orange bird |

Generated originals retain transparency. scripts/export-icons.ps1 converts them into transparent PNGs at 16, 32, 48, 64, 128, and 256 pixels, plus ICO containers holding those six PNG frames. These exports are committed so building the extension does not require an image generation service or an image conversion dependency.

The extension manifest uses icons/visualswift-128.png; each project template uses its corresponding ICO. These assets are VisualSwift artwork rather than official Swift or Microsoft brand assets.
