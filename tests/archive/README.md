# Historical FGS test scripts

This folder preserves five one-off regression scripts from the v5.0.77 film-border and crop development cycle.
They were moved **without modifying their file contents or UTF-8 BOMs**. These historical scripts are **not** invoked by the current formal build or release workflow; they are retained for reference and targeted troubleshooting.

| Script | Original verification scope |
| --- | --- |
| `Test_CropPresetButton.ps1` | Crop preset labels, selection, validation, and saved custom state |
| `Test_FilmBorderClassicVisual.ps1` | Generated Classic Black aperture/perforation appearance |
| `Test_FilmBorderImportSizeMemory.ps1` | Imported border aperture defaults and persisted dimensions |
| `Test_FilmBorderIntegrationReadiness.ps1` | Border source/config integration and original Dark asset checks |
| `Test_FilmBorderUiPolish.ps1` | Cinema default, border button highlight, and right-click clear |

The test files have the same **two-level relative path** to the repository root as they did under `dotnet/BuildTools/`. Run from the repository root on Windows, **after building FGS**, for example:

```powershell
& '.\tests\archive\Test_CropPresetButton.ps1'
```

The Classic Visual check additionally expects `GITHUB_WORKSPACE` to point at the repository root; some historical checks expect `RUNNER_TEMP` to exist. Archived checks may encode old UI contracts; review and update them before bringing one back into CI.

The following seven operational/core regression scripts remain in `dotnet/BuildTools/`:

- `Test_Framework_Api.ps1` and `Test_UI_Startup.ps1`
- `Test_S4_GpuBorder.ps1` and `Test_FilmBorderFraming.ps1`
- `Test_FilmBorderFileSuffix.ps1`, `Test_FilmBorderImportSpec.ps1`, and `Test_FilmBorderAv1UiGuard.ps1`

No encoding pipeline, release structure, or version metadata is changed by this archival move.
