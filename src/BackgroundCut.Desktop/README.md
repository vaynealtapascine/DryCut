# BackgroundCut.Desktop

The WPF workstream owns the user workflow, presentation, drag/drop, dialogs, startup arguments, and single-instance handoff. Infrastructure implementations are intentionally not invented here: `CompositionRoot.cs` contains explicit unavailable seams so a build cannot silently claim to process or export images before those adapters are integrated.

The composition root should replace `UnavailableEngine`, `UnavailableExport`, `UnavailableClipboard`, and `UnavailableCatalog` with Infrastructure implementations during integration.