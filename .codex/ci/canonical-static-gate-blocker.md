# Canonical static gate blocker

Run: https://github.com/rtao-god/aggregator-backend/actions/runs/30962560175
Source SHA: 34be2be3bf49a83d8c1c025816c6153f59bc68d0

| Stage | Outcome |
|---|---|
| preparation | failure |
| owners | skipped |
| restore | skipped |
| format | skipped |
| build | skipped |
| test | skipped |

## preparation (failure)

```text
.github/workflows/repair-architecture-proof-v3.yml:189: SyntaxWarning: invalid escape sequence '\s'
  var start = classLine;
Traceback (most recent call last):
  File "<stdin>", line 20, in <module>
  File ".github/workflows/converge-static-gate.yml", line 29, in <module>
    steps:
  File ".github/workflows/converge-static-gate.yml", line 25, in execute_yaml_python_block
    converge:
      ^^^^^^^^
  File ".github/workflows/repair-architecture-proof-v3.yml", line 359, in <module>
    }
^^^^^
  File ".github/workflows/repair-architecture-proof-v3.yml", line 7, in replace_method
    - '.github/workflows/repair-architecture-proof-v3.yml'
              ^^
NameError: name 're' is not defined. Did you forget to import 're'?

```
