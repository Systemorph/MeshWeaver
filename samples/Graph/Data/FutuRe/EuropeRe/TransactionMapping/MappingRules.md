---
Name: Mapping Rules
Description: EuropeRe local-to-group LoB mapping rules with governance discussion
---

# EuropeRe Transaction Mapping Rules

This document defines how [EuropeRe](@FutuRe/EuropeRe)'s local Lines of Business are allocated to the [group standard classification](@FutuRe/LineOfBusiness). Each mapping specifies a <!--comment:c1-->percentage split<!--/comment:c1-->, enabling one local LoB to contribute to multiple group LoBs.

The mappings are applied virtually at query time by the [Profitability Analysis](@FutuRe/Profitability) hub — <!--comment:c2-->local data is never physically copied to the group level<!--/comment:c2-->.

---

## Mappings

| Local LoB | Group LoB | Split |
|-----------|-----------|-------|
| [Household](@FutuRe/EuropeRe/LineOfBusiness/HOUSEHOLD) | [Property](@FutuRe/LineOfBusiness/PROP) | 90% |
| [Household](@FutuRe/EuropeRe/LineOfBusiness/HOUSEHOLD) | [Casualty](@FutuRe/LineOfBusiness/CAS) | 10% |
| [Motor](@FutuRe/EuropeRe/LineOfBusiness/MOTOR) | [Casualty](@FutuRe/LineOfBusiness/CAS) | 100% |
| [Commercial Fire](@FutuRe/EuropeRe/LineOfBusiness/COMM_FIRE) | [Property](@FutuRe/LineOfBusiness/PROP) | 80% |
| [Commercial Fire](@FutuRe/EuropeRe/LineOfBusiness/COMM_FIRE) | [Energy](@FutuRe/LineOfBusiness/ENRG) | 20% |
| [Liability](@FutuRe/EuropeRe/LineOfBusiness/LIABILITY) | [Casualty](@FutuRe/LineOfBusiness/CAS) | 70% |
| [Liability](@FutuRe/EuropeRe/LineOfBusiness/LIABILITY) | [Professional Liability](@FutuRe/LineOfBusiness/PROF) | 30% |
| [Transport](@FutuRe/EuropeRe/LineOfBusiness/TRANSPORT) | [Marine](@FutuRe/LineOfBusiness/MARINE) | 100% |
| [Technology Risk](@FutuRe/EuropeRe/LineOfBusiness/TECH_RISK) | [Cyber](@FutuRe/LineOfBusiness/CYBER) | 60% |
| [Technology Risk](@FutuRe/EuropeRe/LineOfBusiness/TECH_RISK) | [Professional Liability](@FutuRe/LineOfBusiness/PROF) | 40% |
| [Life & Health](@FutuRe/EuropeRe/LineOfBusiness/LIFE_HEALTH_EU) | [Life & Health](@FutuRe/LineOfBusiness/LH) | 100% |
| [Specialty & Aviation](@FutuRe/EuropeRe/LineOfBusiness/SPECIALTY_AVTN) | [Specialty](@FutuRe/LineOfBusiness/SPEC) | 70% |
| [Specialty & Aviation](@FutuRe/EuropeRe/LineOfBusiness/SPECIALTY_AVTN) | [Aviation](@FutuRe/LineOfBusiness/AVTN) | 30% |

---

## Design Principles

- **No data copying** — local data stays in EuropeRe databases; group view is purely virtual
- **Percentage integrity** — splits for each local LoB always sum to 100%
- **Annual review** — actuarial teams propose adjustments before each fiscal year
