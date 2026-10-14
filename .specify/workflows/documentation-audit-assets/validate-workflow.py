"""Static validation + guard tests in disposable fixtures; never run the workflow."""
from __future__ import annotations

import hashlib
import json
import re
import shutil
import subprocess
import tempfile
from importlib.metadata import version
from pathlib import Path

from specify_cli.integrations import get_integration, load_installed_integrations
from specify_cli.workflows import BUILTIN_STEP_TYPES, get_step_type
from specify_cli.workflows.engine import WorkflowEngine
from specify_cli.workflows.expressions import evaluate_expression
from specify_cli.workflows.base import StepContext


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


assets = Path(__file__).resolve().parent
root = assets.parents[2]
workflow_path = assets.parent / "documentation-audit.yml"
require(version("specify-cli") == "1.2.0", "Required installed specify-cli == 1.2.0")
engine = WorkflowEngine(root)
definition = engine.load_workflow(workflow_path)
errors = engine.validate(definition)
require(not errors, "Native validation failed: " + "; ".join(errors))
require(set(definition.data) <= {"schema_version", "workflow", "requires", "inputs", "steps"}, "Unknown top-level field")
allowed = {
    "shell": {"id", "type", "run", "timeout", "output_format"},
    "prompt": {"id", "type", "prompt", "integration", "timeout"},
    "gate": {"id", "type", "message", "show_file", "options", "on_reject", "verdict_input"},
}
for step in definition.steps:
    kind = step["type"]
    require(kind in BUILTIN_STEP_TYPES and kind in allowed, f"Unsupported step: {kind}")
    require(set(step) <= allowed[kind], f"Unsupported property in {step['id']}")
    require(not get_step_type(kind).validate(step), f"Step validation failed: {step['id']}")
    if kind == "prompt":
        require(step["integration"] == "codex", "Prompt integration must be Codex")
        require("audit-policy.md" in step["prompt"], "Missing policy reference")

load_installed_integrations(root)
codex = get_integration("codex")
require(codex is not None, "Codex integration not available")
argv = codex.build_exec_args("STATIC_VALIDATION_ONLY", project_root=root, output_json=False)
require(bool(argv) and argv[1] == "exec", "Codex prompt dispatch not supported")
require(shutil.which(argv[0]) is not None, "Codex executable not resolvable by the installed dispatcher")

lock = json.loads((assets / "baseline.lock.json").read_text(encoding="utf-8"))
before = {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in lock["files"]}
require(before == lock["files"], "Protected baseline differs from validated lock")
task_ids = re.findall(r"^\|\s*(M\d+-\d+)\s*\|", (root / "AI-Project-Documentation-Tasks.md").read_text(encoding="utf-8"), re.M)
require(len(task_ids) == len(set(task_ids)) == 59, "Unexpected task baseline")

pwsh = shutil.which("pwsh")
require(pwsh is not None, "PowerShell 7 required")
quoted = str(assets / "audit-guards.ps1").replace("'", "''")
parser = subprocess.run([pwsh, "-NoLogo", "-NoProfile", "-Command",
    "$tokens=$null; $parseErrors=$null; "
    f"[System.Management.Automation.Language.Parser]::ParseFile('{quoted}',[ref]$tokens,[ref]$parseErrors) | Out-Null; "
    "if ($parseErrors.Count) { $parseErrors | Out-String | Write-Error; exit 1 }"], capture_output=True, text=True)
require(parser.returncode == 0, "PowerShell syntax failed: " + parser.stderr)

# The helper runs only inside a temporary fixture. No agent or workflow is executed.
checks = []
with tempfile.TemporaryDirectory(prefix="documentation-audit-validation-") as temp:
    fixture = Path(temp)
    for name in list(lock["files"]) + [".specify/init-options.json", ".specify/integration.json"]:
        target = fixture / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / name, target)
    fixture_assets = fixture / ".specify/workflows/documentation-audit-assets"
    fixture_assets.mkdir(parents=True)
    for name in ["baseline.lock.json", "audit-guards.ps1"]:
        shutil.copyfile(assets / name, fixture_assets / name)
    guard = fixture_assets / "audit-guards.ps1"

    def invoke(*args: str, success: bool = True, expected: str = "") -> str:
        result = subprocess.run([pwsh, "-NoLogo", "-NoProfile", "-File", str(guard), *args],
                                cwd=fixture, capture_output=True, text=True, timeout=120)
        require((result.returncode == 0) == success, f"Guard test unexpected result: {args}: {result.stdout} {result.stderr}")
        require(not expected or expected in result.stderr, f"Unexpected failure reason: {result.stderr}")
        return result.stdout

    initial = json.loads(invoke("-Mode", "Initialize"))
    session = fixture / initial["audit_dir"]
    common = ["-AuditDir", initial["audit_dir"], "-SnapshotSha256", initial["snapshot_sha256"]]
    invoke("-Mode", "Verify", "-Stage", "1", *common, success=False, expected="Report mancante")
    checks.append("missing report rejected")
    for name in ["01-requirements-consistency.md", "02-architecture-review.md", "03-task-dependencies.md", "05-consolidated-findings.md"]:
        (session / name).write_text("# Fixture report\n\nValidation fixture only.\n", encoding="utf-8")
    readiness = session / "04-ai-task-readiness.md"
    readiness.write_text("# Readiness fixture\n\n" + "\n".join(f"| {task} | READY | fixture |" for task in task_ids[:-1]), encoding="utf-8")
    invoke("-Mode", "Verify", "-Stage", "3", *common, success=False, expected="numero di righe")
    checks.append("incomplete readiness rejected")
    readiness.write_text(readiness.read_text(encoding="utf-8") + f"\n| {task_ids[-1]} | READY | fixture |\n", encoding="utf-8")
    protected = fixture / "AI-Project-Documentation-MVP-Plan.md"
    original = protected.read_bytes()
    protected.write_bytes(original + b"\nfixture mutation\n")
    invoke("-Mode", "Verify", "-Stage", "4", *common, success=False, expected="Scritture fuori")
    protected.write_bytes(original)
    checks.append("baseline modification rejected")
    stray = fixture / "unexpected-code.txt"
    stray.write_text("fixture", encoding="utf-8")
    invoke("-Mode", "Verify", "-Stage", "4", *common, success=False, expected="ADDED unexpected-code.txt")
    stray.unlink()
    checks.append("out-of-scope addition rejected")
    snapshot = session / "project-baseline.json"
    original_snapshot = snapshot.read_bytes()
    snapshot.write_bytes(original_snapshot + b" ")
    invoke("-Mode", "Verify", "-Stage", "4", *common, success=False, expected="Snapshot di controllo alterato")
    snapshot.write_bytes(original_snapshot)
    checks.append("snapshot tampering rejected")
    invoke("-Mode", "Verify", "-Stage", "4", *common)
    final = json.loads(invoke("-Mode", "Final", "-Stage", "4", *common))
    review_args = ["-Mode", "RecordReview", "-Stage", "4", *common,
                   "-ReportManifestSha256", final["report_manifest_sha256"]]
    report = session / "01-requirements-consistency.md"
    report_bytes = report.read_bytes()
    report.write_bytes(report_bytes + b"\nfixture mutation\n")
    invoke(*review_args, success=False, expected="Report alterato durante")
    report.write_bytes(report_bytes)
    checks.append("report tampering during review rejected")
    invoke(*review_args)
    require((session / "07-human-review.md").is_file(), "Review record not created")
    checks.append("successful final checks and review record in fixture")

context = StepContext(steps={
    "baseline": {"output": {"data": initial}},
    "final-checks": {"output": {"data": final}},
})
for step in definition.steps:
    for key in ["run", "prompt", "message", "show_file"]:
        if key in step:
            resolved = evaluate_expression(step[key], context)
            require(isinstance(resolved, str) and "{{" not in resolved, f"Unresolved expression: {step['id']}.{key}")
after = {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in lock["files"]}
require(after == before, "Baseline changed during validation")
print(json.dumps({"spec_kit": version("specify-cli"), "workflow": str(workflow_path),
    "native_validation": "PASS", "supported_types": sorted(allowed), "steps": len(definition.steps),
    "expressions": "PASS", "baseline_sha256": "UNCHANGED", "guard_fixture_tests": checks,
    "workflow_executed": False}, indent=2))
