# Getting Started with VisionIQ

Welcome! This guide will get you up to speed in 15 minutes.

## What is VisionIQ?

VisionIQ is an engineering intelligence platform that reads approved engineering history (Azure DevOps, Confluence, Git) and stores it as versioned, permission-aware evidence.

**Current Phase:** LEARN (LRN-01 through LRN-07) — **✅ Complete**

See [System Architecture Overview](ARCHITECTURE-OVERVIEW.md) for a visual introduction.

---

## 🚀 First 5 Minutes

### 1. Understand the LEARN Phase
VisionIQ's LEARN phase consists of 7 features that work together:

| Feature | What It Does |
|---------|-------------|
| **LRN-01** | Reads Azure DevOps history (work items, PRs, commits, builds) |
| **LRN-02** | Imports Confluence design decisions with ACL enforcement |
| **LRN-03** | Stores evidence as immutable, versioned records |
| **LRN-04** | Links tickets to the fixes that closed them |
| **LRN-05** | Keeps evidence current via webhooks + scheduled sync |
| **LRN-06** | Scans code for symbols, imports, dependencies |
| **LRN-07** | Builds relationship graph connecting tickets→code→symbols |

**[Read full descriptions](../LEARN-PHASE-COMPLETE/README.md)**

### 2. See It in Action
Look at [System Architecture Overview](ARCHITECTURE-OVERVIEW.md) — shows how data flows from sources → VisionIQ → storage.

### 3. Pick Your Path

**I'm a developer:**
→ [Developer Guides](../DEVELOPER-GUIDES/)

**I need to set up VisionIQ:**
→ [Operations & Infrastructure](../OPERATIONS-AND-INFRASTRUCTURE/)

**I want to understand design decisions:**
→ [Review & Audit](../REVIEW-AND-AUDIT/)

**I need to troubleshoot:**
→ [Troubleshooting Guide](../OPERATIONS-AND-INFRASTRUCTURE/TROUBLESHOOTING/BY-FEATURE.md)

**I want technical reference:**
→ [Reference Materials](../REFERENCE-MATERIALS/)

---

## 📚 Documentation Structure

```
START-HERE/                    ← You are here
├── README.md                  Getting started
├── ARCHITECTURE-OVERVIEW.md   System diagram
└── GLOSSARY.md               Terms and acronyms

LEARN-PHASE-COMPLETE/          ✅ Complete features
├── LRN-01/ through LRN-07/    Each with architecture, testing, acceptance

OPERATIONS-AND-INFRASTRUCTURE/ Setup, deployment, troubleshooting
REFERENCE-MATERIALS/           Technical reference
DEVELOPER-GUIDES/              For contributors
REVIEW-AND-AUDIT/              Design decisions & findings
```

See [Complete Map](../DOCS-MAP.md) for full structure.

---

## 🎯 Common Tasks

### "How do I run the tests?"
[LRN-01-07 Testing Guide](../LEARN-PHASE-COMPLETE/README.md) → each feature has a LOCAL-TESTING.md

### "How do I set up my local environment?"
[Local Environment Setup](../OPERATIONS-AND-INFRASTRUCTURE/README.md)

### "What does feature X do?"
[LEARN Phase](../LEARN-PHASE-COMPLETE/README.md) → pick a feature

### "Why was decision Y made?"
[Decision Log](../REVIEW-AND-AUDIT/DECISION-LOG.md)

### "Something's not working, help!"
[Troubleshooting by Feature](../OPERATIONS-AND-INFRASTRUCTURE/TROUBLESHOOTING/BY-FEATURE.md)

### "I want to contribute"
[Developer Guides](../DEVELOPER-GUIDES/)

---

## 🔑 Key Concepts

**Evidence:** Immutable records of engineering history (work items, PRs, commits) with versions and ACLs

**Source:** Where evidence comes from (Azure DevOps, Confluence, Git)

**Checkpoint:** Cursor tracking what's been synced so re-runs are fast

**Retraction:** When a work item removes an association (e.g., "this is no longer linked to that PR")

**Revert:** When a commit undoes another commit's changes

**Revision:** A version of an evidence item (e.g., work item revision 5)

See [Glossary](GLOSSARY.md) for all terms.

---

## 📖 Recommended Reading Order

1. **This page** (5 min)
2. [Architecture Overview](ARCHITECTURE-OVERVIEW.md) (5 min)
3. **Then pick:**
   - Developer? → [Developer Guides](../DEVELOPER-GUIDES/ONBOARDING.md)
   - Operations? → [Operations](../OPERATIONS-AND-INFRASTRUCTURE/README.md)
   - Review? → [LEARN Phase](../LEARN-PHASE-COMPLETE/README.md)

---

## ❓ FAQ

**Q: Is LEARN complete?**  
A: ✅ Yes. All LRN-01-07 features are implemented, tested, and accepted.

**Q: What's the status of LRN-08+?**  
A: 🔮 Planned future work. See [Planned Features](../PLANNED-FEATURES/).

**Q: Where are the old docs?**  
A: 📦 Historical docs moved to [Archive](../ARCHIVE/).

**Q: How do I find something?**  
A: Start with [main README](../README.md) or [Complete Map](../DOCS-MAP.md).

---

## 🚀 Next Steps

1. Read [Architecture Overview](ARCHITECTURE-OVERVIEW.md)
2. Pick your path above (developer / operations / review)
3. Follow the guide for your path

---

**Questions?** Check [Troubleshooting](../OPERATIONS-AND-INFRASTRUCTURE/TROUBLESHOOTING/) or [Glossary](GLOSSARY.md).
