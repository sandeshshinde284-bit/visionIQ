# VisionIQ — Operations and manual deployment

**Updated:** 11 November 2026 · No blanket cloud acceptance claim.

## Working guides

| Task | Current documentation |
|---|---|
| First-time local environment | [Developer Team Guide](../START-HERE/VisionIQ_Developer_Team_Guide.md) · [repository setup](../../README.md) |
| Database migrations and roles | [Database README](../../db/README.md) |
| Approved manual Cloud Run build/deploy | [FND-04 manual deployment](fnd04-manual-deployment.md) · [GCP setup guide](../Functional/VisionIQ_Google_Cloud_Setup_Deployment_Guide.md) |
| ADO PR webhook image and approved ingress | [LRN-12 webhook deployment](LRN12-WEBHOOK-CLOUD-DEPLOYMENT.md) |
| Vertex embeddings | [Vertex AI setup](VERTEX-AI-SETUP.md) |
| Manual LEARN LRN-01–13 scope | [LEARN handbook](../LEARN-PHASE-COMPLETE/LEARN-HANDBOOK.md) |
| Targeted manual fix/revert lessons | [LRN-13 manual acceptance](../LEARN-PHASE-COMPLETE/LRN-13-MANUAL-LEARNING-ACCEPTANCE.md) |
| Logging, metrics, failures | [Logging and monitoring](LOGGING-AND-MONITORING.md) · [reliable events troubleshooting](TROUBLESHOOTING/fnd05-reliable-events.md) |

## Deployment safety

- Use reviewed commit-tagged images and explicit source/database/project restrictions. Confirm the actual Cloud SQL address, database, service-account grants, secrets and private networking *before* deploying.
- Existing API/Intelligence Cloud Run services are internal/IAM-protected. Do **not** make them public just to receive ADO hooks; that needs its own approved ingress/security decision.
- A manual run still needs passing migrations, service health, real source connectivity, indexed-snapshot integrity, cited Brief output and safe defect outcome. Cloud deployment and acceptance are distinct steps.
- Do not run disposable database integration fixtures against the published Genoa Kiosk evidence database.
