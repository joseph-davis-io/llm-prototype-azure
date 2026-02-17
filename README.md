# llm-prototype-azure

Prototype repository for fine-tuning and deploying an Azure OpenAI model using Azure AI Foundry.

## What this repo does

- Stores chat fine-tuning datasets as JSONL in `data/` (for example `data/train.jsonl` and `data/valid.jsonl`).
- Validates JSONL formatting via `scripts/validate_jsonl.py`.
- Automates fine-tuning when training data changes using GitHub Actions workflows in `.github/workflows/`.
  - On pushes to `main` that change `data/**/*.jsonl`, the workflow uploads the training/validation files to Azure OpenAI and starts a fine-tuning job.
  - The fine-tuning job id is persisted as a build artifact for traceability.

## GitHub Actions workflows

Workflows live in `.github/workflows/` and are intended to be run in GitHub Actions (not locally):

- `foundry-finetune-on-jsonl-change.yml`: triggers a fine-tuning job when JSONL data changes.
- `foundry-deploy-finetuned-on-completion.yml`: intended to deploy/use the fine-tuned model once a job completes (implementation depends on your Azure setup).

## Configuration

The workflows expect these GitHub repository secrets:

- `AZURE_OPENAI_ENDPOINT`: your Azure OpenAI resource endpoint.
- `AZURE_OPENAI_API_KEY`: an API key for the resource.

The model name and API version are currently set in the workflow YAML and can be changed there.
