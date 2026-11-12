# VisionIQ Documentation

Welcome to VisionIQ! This folder contains all documentation organized by purpose and phase.

## 🚀 Quick Start

**New to VisionIQ?** Start here:
- [Getting Started Guide](START-HERE/README.md) — First 15 minutes
- [System Architecture Overview](START-HERE/ARCHITECTURE-OVERVIEW.md) — High-level diagram
- [Glossary](START-HERE/GLOSSARY.md) — Terms and acronyms

## 📚 Documentation by Purpose

### **🎓 LEARN Phase** (Currently Active)
Complete documentation for LRN-01 through LRN-07 features.
- **Status:** ✅ Complete and verified
- **Read:** [LEARN Phase Documentation](LEARN-PHASE-COMPLETE/README.md)
- Includes: Architecture, implementation, testing, acceptance criteria

### **📝 BRIEF Phase** (BRF-01 to BRF-08)
The ticket brief: pickup, model use, brief content, delivery on the ticket, ratings, speed and hotspots.
- **Status:** BRF-01 merged; BRF-02 to BRF-08 implemented and tested locally (offline model stand-in)
- **Read:** [BRIEF architecture and local test walkthrough](features/BRIEF-ARCHITECTURE.md)
- Per story: [features/](features/) (one folder per story, plus FND-05 reliable events)

### **🛠️ DEFECT Phase** (DEF-01 to DEF-06)
Reproduce a defect with a failing test, propose a bounded fix, prove it, and prepare a draft PR.
- **Status:** implemented and tested locally on both sides with fakes in place of Azure DevOps and the model; not yet run against a real organization, model or database
- **Read:** [DEFECT architecture and local walkthrough](features/DEFECT-ARCHITECTURE.md)
- Per story: [features/](features/) (`DEF-01-…` to `DEF-06-…`); test steps in [TESTING-KICKOFF-TODAY.md](TESTING-KICKOFF-TODAY.md), step 2C

### **🔮 Planned Features** (Future Work)
Upcoming features and infrastructure components.
- **Status:** 🚧 In development or planned
- **Read:** [Planned Features](PLANNED-FEATURES/README.md)
- Includes: LRN-08 retrieval, LRN-09+ search, FND series

### **🛠️ Operations & Infrastructure**
Setup, deployment, troubleshooting, and operations guides.
- **Read:** [Operations Guide](OPERATIONS-AND-INFRASTRUCTURE/README.md)
- Includes: Local setup, Docker, database, cloud, troubleshooting

### **📖 Reference Materials**
Technical reference documentation.
- **Read:** [Reference Materials](REFERENCE-MATERIALS/README.md)
- Includes: Schema, API contracts, data models, security model

### **✅ Quality Assurance**
Testing strategy, acceptance criteria, and verification.
- **Read:** [QA Documentation](QUALITY-ASSURANCE/README.md)
- Includes: Test levels, acceptance matrix, benchmarks

### **🎯 Developer Guides**
For developers contributing to VisionIQ.
- **Read:** [Developer Guides](DEVELOPER-GUIDES/README.md)
- Includes: Onboarding, contributing, debugging, code style

### **📋 Review & Audit**
Design decisions, findings, and audit trail.
- **Read:** [Review & Audit](REVIEW-AND-AUDIT/README.md)
- Includes: Decision log, findings, integration review results

### **📦 Archive**
Historical and deprecated documentation.
- **Status:** 📚 Historical reference only
- **Read:** [Archive](ARCHIVE/README.md)

---

## 🗺️ Complete Structure

```
docs/
├── START-HERE/                      Getting started guides
├── LEARN-PHASE-COMPLETE/            LRN-01 through LRN-07 (✅ Complete)
│   ├── LRN-01-HISTORY-CONNECTORS/
│   ├── LRN-02-CONFLUENCE/
│   ├── LRN-03-VERSIONED-EVIDENCE/
│   ├── LRN-04-TICKET-FIX-LINKS/
│   ├── LRN-05-BACKFILL-AND-SYNC/
│   ├── LRN-06-CODE-INTELLIGENCE/
│   └── LRN-07-RELATIONSHIP-GRAPH/
├── PLANNED-FEATURES/                Future work (🔮 In development)
├── OPERATIONS-AND-INFRASTRUCTURE/   Setup, deployment, troubleshooting
├── REFERENCE-MATERIALS/             Technical reference
├── QUALITY-ASSURANCE/               Testing and acceptance
├── DEVELOPER-GUIDES/                For contributors
├── REVIEW-AND-AUDIT/                Decisions and findings
└── ARCHIVE/                         Historical docs (📦 Archived)
```

See [Complete Documentation Map](DOCS-MAP.md) for details.

---

## 📍 Status Indicators

- ✅ **COMPLETE** — Feature finished and verified
- 🔮 **PLANNED** — Feature in development or planned
- 🚧 **IN PROGRESS** — Active work
- ⚠️ **STALE** — Needs update
- 📦 **ARCHIVED** — Historical reference only

---

## 🔗 Quick Links

| I want to... | Go to |
|-------------|--------|
| Get started | [START-HERE](START-HERE/) |
| Understand LRN-01-07 | [LEARN Phase](LEARN-PHASE-COMPLETE/) |
| Troubleshoot an issue | [Troubleshooting Guide](OPERATIONS-AND-INFRASTRUCTURE/TROUBLESHOOTING/) |
| Understand design decisions | [Decision Log](REVIEW-AND-AUDIT/) |
| Set up my environment | [Operations Guide](OPERATIONS-AND-INFRASTRUCTURE/) |
| Contribute to VisionIQ | [Developer Guides](DEVELOPER-GUIDES/) |
| Check API/Schema | [Reference Materials](REFERENCE-MATERIALS/) |

---

## 📞 Need Help?

1. Check [START-HERE](START-HERE/) for common questions
2. Search [Troubleshooting Guide](OPERATIONS-AND-INFRASTRUCTURE/TROUBLESHOOTING/BY-FEATURE.md)
3. Review [Reference Materials](REFERENCE-MATERIALS/)
4. Check [Decision Log](REVIEW-AND-AUDIT/DECISION-LOG.md) for why decisions were made

---

**Last Updated:** November 5, 2026  
**Documentation Version:** 2.0 (Restructured)
