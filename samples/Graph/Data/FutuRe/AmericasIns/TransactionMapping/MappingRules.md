---
Name: Mapping Rules
Description: AmericasIns local-to-group LoB mapping rules with governance discussion
---

# AmericasIns Transaction Mapping Rules

This document defines how [AmericasIns](@FutuRe/AmericasIns)'s local Lines of Business are allocated to the [group standard classification](@FutuRe/LineOfBusiness). Each mapping specifies a <!--comment:c1-->percentage split<!--/comment:c1-->, enabling one local LoB to contribute to multiple group LoBs.

The mappings are applied virtually at query time by the [Profitability Analysis](@FutuRe/Profitability) hub — local data is never physically copied to the group level.

---

## Mappings

| Local LoB | Group LoB | Split |
|-----------|-----------|-------|
| [Homeowners](@FutuRe/AmericasIns/LineOfBusiness/HOMEOWNERS) | [Property](@FutuRe/LineOfBusiness/PROP) | 85% |
| [Homeowners](@FutuRe/AmericasIns/LineOfBusiness/HOMEOWNERS) | [Casualty](@FutuRe/LineOfBusiness/CAS) | 15% |
| [Workers Compensation](@FutuRe/AmericasIns/LineOfBusiness/WORKERS_COMP) | [Casualty](@FutuRe/LineOfBusiness/CAS) | 100% |
| <!--comment:c2-->[Commercial Lines](@FutuRe/AmericasIns/LineOfBusiness/COMMERCIAL)<!--/comment:c2--> | [Property](@FutuRe/LineOfBusiness/PROP) | 60% |
| [Commercial Lines](@FutuRe/AmericasIns/LineOfBusiness/COMMERCIAL) | [Casualty](@FutuRe/LineOfBusiness/CAS) | 25% |
| [Commercial Lines](@FutuRe/AmericasIns/LineOfBusiness/COMMERCIAL) | [Marine](@FutuRe/LineOfBusiness/MARINE) | 15% |
| [Energy & Mining](@FutuRe/AmericasIns/LineOfBusiness/ENERGY_MINING) | [Energy](@FutuRe/LineOfBusiness/ENRG) | 80% |
| [Energy & Mining](@FutuRe/AmericasIns/LineOfBusiness/ENERGY_MINING) | [Property](@FutuRe/LineOfBusiness/PROP) | 20% |
| [Life & Annuity](@FutuRe/AmericasIns/LineOfBusiness/LIFE_ANN) | [Life & Health](@FutuRe/LineOfBusiness/LH) | 100% |
| [Cyber & Technology](@FutuRe/AmericasIns/LineOfBusiness/CYBER_TECH) | [Cyber](@FutuRe/LineOfBusiness/CYBER) | 70% |
| [Cyber & Technology](@FutuRe/AmericasIns/LineOfBusiness/CYBER_TECH) | [Professional Liability](@FutuRe/LineOfBusiness/PROF) | 30% |
| [Specialty & Aviation](@FutuRe/AmericasIns/LineOfBusiness/SPECIALTY_AVTN_US) | [Specialty](@FutuRe/LineOfBusiness/SPEC) | 50% |
| [Specialty & Aviation](@FutuRe/AmericasIns/LineOfBusiness/SPECIALTY_AVTN_US) | [Aviation](@FutuRe/LineOfBusiness/AVTN) | 50% |
| [Agriculture](@FutuRe/AmericasIns/LineOfBusiness/AGRICULTURE) | [Agriculture](@FutuRe/LineOfBusiness/AGRI) | 100% |

---

## Design Principles

- **No data copying** — local data stays in AmericasIns databases; group view is purely virtual
- **Percentage integrity** — splits for each local LoB always sum to 100%
- **Annual review** — actuarial teams propose adjustments before each fiscal year
