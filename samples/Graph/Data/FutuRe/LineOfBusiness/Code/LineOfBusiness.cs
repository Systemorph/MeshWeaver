// <meshweaver>
// Id: LineOfBusiness
// DisplayName: Line of Business
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Line of business dimension for insurance/reinsurance classification.
/// Each line of business represents a distinct category of risk that insurers
/// and reinsurers underwrite, with its own regulatory framework, actuarial
/// models, and market dynamics.
/// </summary>
public record LineOfBusiness : Dimension
{
    /// <summary>
    /// Detailed description of the line of business, including the types of
    /// risks covered and typical policy structures.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order in lists and reports.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Comma-separated list of typical insurance products mapped to this
    /// line of business, used for categorizing legal entity portfolios.
    /// </summary>
    public string? ProductExamples { get; init; }

    public static readonly LineOfBusiness Property = new()
    {
        SystemName = "PROP",
        DisplayName = "Property",
        Description = "Property insurance covers physical assets against damage or loss from perils such as fire, storm, flood, earthquake, and other natural or man-made catastrophes. Policies typically protect commercial buildings, industrial facilities, residential properties, and their contents. Business interruption coverage compensates for lost income when insured premises become unusable. Property reinsurance is one of the largest global reinsurance markets, with catastrophe excess-of-loss treaties forming a critical component of capacity management.",
        Order = 0,
        ProductExamples = "Commercial property, Homeowners, Business interruption, Builders risk, Boiler & machinery, Difference in conditions"
    };

    public static readonly LineOfBusiness Casualty = new()
    {
        SystemName = "CAS",
        DisplayName = "Casualty",
        Description = "Casualty insurance addresses liability arising from bodily injury or property damage caused to third parties. This line encompasses general liability, automobile liability, workers compensation, employers liability, and umbrella/excess liability. Casualty risks are characterized by long-tail development patterns where claims may take years to fully emerge and settle. Reinsurance structures often include both proportional treaties and excess-of-loss layers to manage volatility in large or mass tort exposures.",
        Order = 1,
        ProductExamples = "General liability, Workers compensation, Auto liability, Umbrella/excess, Employers liability, Products liability"
    };

    public static readonly LineOfBusiness Marine = new()
    {
        SystemName = "MARINE",
        DisplayName = "Marine",
        Description = "Marine insurance is one of the oldest forms of insurance, covering risks associated with maritime commerce and navigation. Hull insurance protects vessel owners against physical damage to ships. Cargo insurance covers goods in transit by sea, air, or land. Protection & Indemnity (P&I) provides third-party liability coverage for shipowners. Inland marine extends coverage to goods transported over land and specialized movable property. The marine market operates globally with significant concentration in London and Singapore.",
        Order = 2,
        ProductExamples = "Ocean hull, Cargo, Protection & Indemnity, Inland marine, Yacht, Marine liability, War risks"
    };

    public static readonly LineOfBusiness Aviation = new()
    {
        SystemName = "AVTN",
        DisplayName = "Aviation",
        Description = "Aviation insurance covers risks related to aircraft operation, including hull damage, passenger and third-party liability, and airport operations. This highly specialized line requires deep technical expertise in aircraft types, pilot qualifications, and airworthiness standards. The aviation market also encompasses space risks, including satellite launch and in-orbit coverage. Due to the catastrophic potential of aviation losses, reinsurance plays a vital role in providing capacity. The market is concentrated among a small number of specialist underwriters worldwide.",
        Order = 3,
        ProductExamples = "Aircraft hull, Aviation liability, Airport operators, Space launch, Satellite in-orbit, Manufacturers liability, War & terrorism"
    };

    public static readonly LineOfBusiness Energy = new()
    {
        SystemName = "ENRG",
        DisplayName = "Energy",
        Description = "Energy insurance covers the oil & gas, power generation, mining, and renewable energy sectors. Upstream risks include exploration and production platforms, drilling rigs, and pipelines. Downstream covers refineries, petrochemical plants, and distribution networks. Power generation insurance protects conventional and renewable energy facilities. The energy line is characterized by high-value individual risks, complex engineering assessments, and significant natural catastrophe exposure. Losses can be among the largest in the insurance industry.",
        Order = 4,
        ProductExamples = "Upstream oil & gas, Downstream refining, Power generation, Renewable energy, Mining, Construction (energy), Control of well"
    };

    public static readonly LineOfBusiness LifeAndHealth = new()
    {
        SystemName = "LH",
        DisplayName = "Life & Health",
        Description = "Life & Health insurance provides financial protection against mortality, morbidity, and longevity risks. Life insurance pays benefits upon death or survival to a specified age. Health insurance covers medical expenses, disability income, and long-term care. Accident & health products provide lump-sum or periodic payments for accidental injury or illness. Life reinsurance enables primary insurers to manage portfolio concentration and capital requirements. Longevity risk transfer has become increasingly important as populations age globally.",
        Order = 5,
        ProductExamples = "Term life, Whole life, Group life, Disability income, Medical expense, Accident & health, Longevity swaps, Critical illness"
    };

    public static readonly LineOfBusiness Specialty = new()
    {
        SystemName = "SPEC",
        DisplayName = "Specialty",
        Description = "Specialty insurance encompasses niche and complex risks that require specialized underwriting expertise beyond standard property and casualty lines. Political risk insurance protects against government actions such as expropriation, currency inconvertibility, and political violence. Trade credit covers non-payment by commercial buyers. Surety bonds guarantee contractual performance. Other specialty lines include kidnap & ransom, fine art, contingency, and intellectual property insurance. These products often require bespoke policy wordings and tailored risk assessment.",
        Order = 6,
        ProductExamples = "Political risk, Trade credit, Surety bonds, Kidnap & ransom, Fine art, Contingency, Intellectual property, Title insurance"
    };

    public static readonly LineOfBusiness Cyber = new()
    {
        SystemName = "CYBER",
        DisplayName = "Cyber",
        Description = "Cyber insurance is one of the fastest-growing lines, covering losses arising from data breaches, ransomware attacks, system failures, and other technology-related perils. First-party coverage addresses the insured's own losses including data restoration, business interruption, and crisis management costs. Third-party coverage protects against privacy liability, regulatory fines, and network security claims. The cyber market faces unique challenges including systemic risk from widespread attacks, rapidly evolving threat landscapes, and limited historical loss data for actuarial modeling.",
        Order = 7,
        ProductExamples = "Data breach response, Ransomware, Cyber business interruption, Privacy liability, Network security, Media liability, Technology E&O"
    };

    public static readonly LineOfBusiness ProfessionalLiability = new()
    {
        SystemName = "PROF",
        DisplayName = "Professional Liability",
        Description = "Professional liability insurance protects professionals and their firms against claims of negligence, errors, or omissions in the performance of professional services. Directors & Officers (D&O) insurance shields corporate leaders from personal liability arising from management decisions. Errors & Omissions (E&O) covers professional service providers including lawyers, architects, and consultants. Medical malpractice addresses claims against healthcare providers. These lines feature claims-made policy forms and often involve complex litigation with significant defense costs.",
        Order = 8,
        ProductExamples = "Directors & Officers, Errors & Omissions, Medical malpractice, Fiduciary liability, Employment practices, Accountants liability"
    };

    public static readonly LineOfBusiness Agricultural = new()
    {
        SystemName = "AGRI",
        DisplayName = "Agricultural",
        Description = "Agricultural insurance protects farmers, agribusinesses, and food supply chains against weather-related perils, pest damage, disease outbreaks, and market price fluctuations. Crop insurance is the largest segment, often supported by government subsidy programs. Livestock insurance covers mortality and disease in animal husbandry. Index-based weather insurance uses parametric triggers tied to rainfall, temperature, or satellite vegetation indices rather than traditional loss adjustment. Agricultural reinsurance helps manage the high correlation of weather-driven losses across geographic regions.",
        Order = 9,
        ProductExamples = "Multi-peril crop, Crop hail, Livestock mortality, Aquaculture, Forestry, Index-based weather, Revenue protection, Greenhouse"
    };

    public static readonly LineOfBusiness[] All =
    [
        Property, Casualty, Marine, Aviation, Energy,
        LifeAndHealth, Specialty, Cyber, ProfessionalLiability, Agricultural
    ];

    public static LineOfBusiness GetById(string? id) =>
        All.FirstOrDefault(l => l.SystemName == id) ?? Property;
}
