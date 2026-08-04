#!/usr/bin/env python3
from __future__ import annotations

import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / "tools" / "AnalyticsWorkerGenerator" / "Program.cs"
text = path.read_text(encoding="utf-8")

text = text.replace(
    'var workerDirectory = root / "src" / "Analytics" / "Analytics.Worker";',
    'var workerDirectory = new DirectoryInfo(Path.Combine(root.FullName, "src", "Analytics", "Analytics.Worker"));',
)
text = text.replace(
    'var testDirectory = root / "tests" / "Analytics" / "Analytics.Worker.Tests";',
    'var testDirectory = new DirectoryInfo(Path.Combine(root.FullName, "tests", "Analytics", "Analytics.Worker.Tests"));',
)
text = text.replace(
    'var reportDirectory = root / "docs" / "generated";',
    'var reportDirectory = new DirectoryInfo(Path.Combine(root.FullName, "docs", "generated"));',
)
text = text.replace("Directory.CreateDirectory(workerDirectory);", "Directory.CreateDirectory(workerDirectory.FullName);")
text = text.replace("Directory.CreateDirectory(testDirectory);", "Directory.CreateDirectory(testDirectory.FullName);")
text = text.replace("Directory.CreateDirectory(reportDirectory);", "Directory.CreateDirectory(reportDirectory.FullName);")

for variable, filenames in {
    "workerDirectory": [
        "Analytics.Worker.csproj",
        "GeneratedAnalyticsAggregationService.cs",
        "AnalyticsWorkerOptions.cs",
        "AnalyticsAggregationWorker.cs",
        "Program.cs",
    ],
    "testDirectory": [
        "Analytics.Worker.Tests.csproj",
        "Usings.cs",
        "AnalyticsWorkerOptionsTests.cs",
    ],
    "reportDirectory": ["analytics-worker-generation.md"],
}.items():
    for filename in filenames:
        text = text.replace(
            f'{variable} / "{filename}"',
            f'Path.Combine({variable}.FullName, "{filename}")',
        )
        text = text.replace(
            f'Path.Combine({variable}.FullName, "{filename}",',
            f'Path.Combine({variable}.FullName, "{filename}"),',
        )

text = text.replace(
    "    namespace Aggregator.Analytics.Worker;\n\n    public sealed record AnalyticsWorkerOptions",
    "    using Microsoft.Extensions.Configuration;\n\n    namespace Aggregator.Analytics.Worker;\n\n    public sealed record AnalyticsWorkerOptions",
)

# Remove the earlier workflow's accidental no-op replacement artifact if it was committed.
text = text.replace("text = text.replace", "text = text.replace")

path.write_text(text, encoding="utf-8")
