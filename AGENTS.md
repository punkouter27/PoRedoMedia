# Rules for agents working in this repository

Task list: [tasks/todo.md](tasks/todo.md).
Overall summary of the project: [DOCS/README.md](DOCS/README.md).

## 1. Git & workflow

- **Stay on `master`:** Commit directly to `master`. Create branches only when specifically asked.
- **Casual commits:** Write short, natural commit messages in informal American phrasing. On a sync,
  commit and push without asking.

## 2. Running, testing & clean code

- **Startup verification:** After any code change, restart the app and verify it starts successfully.
- **Run only related tests:** Test only what was touched. Skip tests for minor edits; never run the
  whole test suite.
- **Zero warnings:** Fix every build warning right away. Treat every warning as a failure.

## 3. Architecture & setup

- **Keep docs updated:** Read [DOCS/](DOCS/) and [DOCS/README.md](DOCS/README.md) first for a quick
  overview. Update them in the same change whenever dependencies, configuration keys or major
  endpoints change.
- **Fully automated:** Run all builds, scripts and setup commands directly. Do not ask the user to
  run terminal commands or click through a web GUI.
- **Secrets:** Do not use `dotnet user-secrets`. Put local data in `appsettings` or in Azure Key
  Vault (`kv-poshared2`, secrets prefixed `PoRedoMedia--`).
- **Build version:** The app shows its deployment build version as `YYMMDD` in the bottom-right
  corner. `Directory.Build.targets` derives it from the Git commit date (today's date when there is
  no commit); never type it by hand.

## 4. UI & output rules

- **UI previews:** For a visual change, save before-and-after HTML mockups to
  `SCREENSHOTS/{YYMMDD}-{feature}.html`, and also one PNG that shows the annotated before and after
  side by side with notes. Give the full valid paths.
- **Clarifying questions:** After changing more than 100 lines, when no questions are already
  pending, list 3 key follow-up questions about future direction or logic.
- **Big removals:** If a prompt removes more than 100 lines of code overall, say so.
- **Required footers:**
  - **Short recap:** If a response is over 100 words, end it with a recap under 20 words.
  - **3-level summary:** If a code change is over 20 lines, add exactly one sentence per level:
    1. Toddler: explain the idea simply.
    2. Child: explain what the app actually does now.
    3. Adult: explain how it was built.

## Running things

- App: `dotnet run --project src/PoRedoMedia.Api` (http://localhost:4100; needs `docker compose up -d` for Azurite).
- Browser tests: `scripts/run-e2e.ps1`. Test budgets: `scripts/check-test-budgets.ps1`.
- Package for App Service: `bash scripts/package.sh package.zip` (Linux or WSL; the same script `deploy.yml` runs).

## CI/CD and Azure (same section in every Po repo)

- A push to `master` builds, tests, deploys and health-gates (`.github/workflows/deploy.yml`). Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies) and `/health` (dependencies reachable). The deploy's health gate checks both.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
