namespace MeshWeaver.CreativeCloud.Domain.SampleData;

/// <summary>
/// Provides sample data for the CreativeCloud content portal.
/// </summary>
public static class CreativeCloudSampleData
{
    /// <summary>
    /// Gets Roland Bürgi's person record.
    /// </summary>
    public static Person RolandBuergi => new()
    {
        Id = "roland-buergi",
        FirstName = "Roland",
        LastName = "Bürgi",
        Company = "notus GmbH",
        Email = "roland@notus.ch",
        ContentArchetypeId = "roland-archetype"
    };

    /// <summary>
    /// Gets Roland Bürgi's content archetype.
    /// </summary>
    public static ContentArchetype RolandArchetype => new()
    {
        Id = "roland-archetype",
        Name = "Roland Bürgi",
        PurposeStatement = "Transforming enterprises through agentic AI implementations while open-sourcing 25 years of software expertise to accelerate the industry's evolution.",
        TacticalDescription = "Step-by-step guides, how-to articles, and best practices for immediate, actionable advice.",
        AspirationalDescription = "Case studies, testimonials, and real-life examples showcasing transformation stories.",
        InsightfulDescription = "Opinion pieces, trend analyses, and detailed industry reports positioning as authority.",
        PersonalDescription = "Founder stories, personal reflections, and candid updates to build trust."
    };

    /// <summary>
    /// Gets all sample persons.
    /// </summary>
    public static IEnumerable<Person> GetPersons() => [RolandBuergi];

    /// <summary>
    /// Gets all sample content archetypes.
    /// </summary>
    public static IEnumerable<ContentArchetype> GetContentArchetypes() => [RolandArchetype];

    /// <summary>
    /// Gets all content lenses for Roland's archetype.
    /// </summary>
    public static IEnumerable<ContentLens> GetContentLenses() =>
    [
        // Tactical pillar
        new()
        {
            Id = "lens-1",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Tactical",
            DisplayOrder = 1,
            Name = "Enterprise AI Transformation Methodology",
            Description = "How to transition legacy systems to agentic AI workflows, practical implementation steps for MeshWeaver deployment, measurable ROI frameworks for AI adoption decisions.",
            ExamplePosts = "Post 1: \"Most enterprises fail at AI transformation because they try to replace everything at once. Here's the 3-phase approach I use with tier-1 companies that reduces implementation risk by 80% while delivering measurable results.\"\n\nPost 2: \"The biggest mistake I see CTOs make is treating AI like another software tool. Agentic AI requires a completely different mindset. Here are the 5 fundamental shifts your organization needs to make before deploying any AI solution.\""
        },
        new()
        {
            Id = "lens-2",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Tactical",
            DisplayOrder = 2,
            Name = "Data Mesh Architecture Implementation",
            Description = "Decentralized data management best practices, common data mesh pitfalls and solutions, step-by-step Service Level Objectives creation.",
            ExamplePosts = "Post 1: \"After 15 years of building data systems for tier-1 companies, I've learned that centralized data warehouses are the Tower of Babel of enterprise IT. Here's how you can implement data mesh architecture as a scalable solution.\"\n\nPost 2: \"Your data teams are spending 80% of their time copy-pasting between Excel sheets. This isn't just inefficient, it's dehumanizing. Here's the exact framework I use to eliminate manual data work and free your team for strategic thinking.\""
        },
        new()
        {
            Id = "lens-3",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Tactical",
            DisplayOrder = 3,
            Name = "Agentic AI Workflow Design",
            Description = "Templates for AI-powered business processes, integration patterns with existing enterprise systems, prompt engineering for business applications.",
            ExamplePosts = "Post 1: \"Building an AI chatbot that actually works in enterprise environments requires understanding the difference between consumer AI and business AI. Here are the 7 critical considerations most developers miss.\"\n\nPost 2: \"I just reduced a client's pricing process from 2 days to 1 hour using agentic AI. The secret isn't the technology, it's understanding which tasks should never be automated and which ones are begging for AI intervention.\""
        },
        // Aspirational pillar
        new()
        {
            Id = "lens-4",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Aspirational",
            DisplayOrder = 1,
            Name = "Systemorph's Agentic AI Breakthroughs",
            Description = "Breakthrough agentic AI implementations and their transformational impact, innovative open-source releases, next-generation agentic systems reshaping enterprise automation.",
            ExamplePosts = "Post 1: \"18 months ago, agentic AI was mostly theoretical research papers. Last week, our autonomous system completed a full enterprise migration with zero human intervention and 99.7% accuracy. Here's how our team solved the context-switching challenge that every AI company said was years away.\"\n\nPost 2: \"When we decided to open-source 15 years of proprietary enterprise software, every advisor told us we were giving away our competitive advantage. Our recent community adoption numbers prove that sharing knowledge accelerates innovation faster than hoarding it ever could.\""
        },
        new()
        {
            Id = "lens-5",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Aspirational",
            DisplayOrder = 2,
            Name = "Enterprise Transformation Success Stories",
            Description = "Breakthrough client implementations and measurable business impact, strategic partnerships and technology adoption victories, team evolution and talent development.",
            ExamplePosts = "Post 1: \"3 years ago, a Fortune 500 client told us their legacy system migration was impossible without 18 months downtime. Today, our agentic AI completed the same transformation in 6 weeks with zero business interruption and significant cost reduction.\"\n\nPost 2: \"When ChatGPT launched, everyone said consulting was dead and AI would replace human expertise. Systemorph's recent client wins prove that combining 25 years of enterprise knowledge with agentic AI creates value no pure-AI solution can match.\""
        },
        new()
        {
            Id = "lens-6",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Aspirational",
            DisplayOrder = 3,
            Name = "Enterprise AI Visionaries",
            Description = "Inspiring stories of technology leaders who bridged enterprise needs with cutting-edge AI, key elements of breakthrough enterprise AI adoption.",
            ExamplePosts = "Post 1: \"The remarkable journey of Jensen Huang at NVIDIA and what every enterprise AI leader can learn from building infrastructure that enables an entire industry transformation.\"\n\nPost 2: \"Satya Nadella never accepted that enterprise software had to be complex and disconnected. That's why he rebuilt Microsoft's entire strategy around AI-first thinking, and now every enterprise benefits from his vision of intelligent automation.\""
        },
        // Insightful pillar
        new()
        {
            Id = "lens-7",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Insightful",
            DisplayOrder = 1,
            Name = "Agentic AI Implementation Patterns",
            Description = "Emerging agentic AI technologies reshaping enterprise automation, regulatory trends and AI governance, compliance and risk management landscapes.",
            ExamplePosts = "Post 1: \"How does Systemorph stay ahead of innovations in the $50B+ enterprise AI market? We study implementation failures, not just technology demos. The next wave isn't about smarter models - it's about autonomous systems that actually integrate with legacy infrastructure.\"\n\nPost 2: \"These 3 agentic AI patterns every enterprise CTO must consider: human-in-the-loop governance frameworks, European-first privacy compliance strategies, and manufacturing execution tactics adapted for knowledge work automation.\""
        },
        new()
        {
            Id = "lens-8",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Insightful",
            DisplayOrder = 2,
            Name = "Open Source Strategy in Enterprise AI",
            Description = "How open-source communities reshape enterprise AI adoption, evolution from proprietary platforms to collaborative ecosystems, authentic knowledge sharing.",
            ExamplePosts = "Post 1: \"The client who transformed our approach didn't just buy our software - they contributed 15 improvements back to our open-source framework that benefited 200+ other implementations. This community effect is why open-source beats proprietary platforms in enterprise AI every time.\"\n\nPost 2: \"After implementing agentic AI for 50+ enterprise clients, the most successful deployments all prioritize internal capability building over vendor dependency. This insight completely changed how I approach consulting - and it's why 80% of our implementations become self-sustaining within 6 months.\""
        },
        new()
        {
            Id = "lens-9",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Insightful",
            DisplayOrder = 3,
            Name = "Technical Leadership Philosophy",
            Description = "Balancing cutting-edge AI research with production-ready solutions, technical debt in AI system architecture, jazz improvisation principles and adaptive software development.",
            ExamplePosts = "Post 1: \"Traditional enterprise software moves too slowly. AI research moves too fast. The companies winning today have figured out the specific balance that lets them deploy autonomous systems responsibly at startup speed while maintaining enterprise-grade reliability.\"\n\nPost 2: \"Jazz musicians understand something most software architects miss: the best improvisation happens within structured frameworks. This principle from my musical background is why our agentic AI systems can adapt to unexpected scenarios while maintaining predictable business outcomes.\""
        },
        // Personal pillar
        new()
        {
            Id = "lens-10",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Personal",
            DisplayOrder = 1,
            Name = "Build in Public",
            Description = "Real-time updates from scaling Systemorph and pioneering agentic AI implementations, quarterly milestones and technical breakthroughs, transitioning from consulting to AI innovation leadership.",
            ExamplePosts = "Post 1: \"In Q3 of 2024, Systemorph deployed more autonomous AI agents than in the previous five years of traditional consulting combined. It was also the most technically challenging three months of my career - here's why both things are connected.\"\n\nPost 2: \"I'm not naturally drawn to content creation, but when you're open-sourcing 15 years of proprietary technology, authentic documentation is your only way to build trust in a skeptical enterprise market.\""
        },
        new()
        {
            Id = "lens-11",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Personal",
            DisplayOrder = 2,
            Name = "Career Evolution and Technical Leadership Growth",
            Description = "Career milestones from programmer to 70-person company CEO to AI pioneer, reasoning behind major transitions, overcoming technical and political challenges.",
            ExamplePosts = "Post 1: \"At 38 years old I started doing enterprise software consulting, and at 53 I'm pioneering the first agentic AI implementations for tier-1 companies while open-sourcing 15 years of proprietary technology. The bridge between these worlds isn't what most people think - it's understanding that both legacy systems and AI agents need the same thing: reliable, predictable execution.\"\n\nPost 2: \"I never thought that while becoming CEO of a 70-person company, the biggest challenge would be losing my passion for programming and having to fight politics instead of solving technical problems. Turns out, rediscovering that coding passion through AI innovation was exactly what my leadership needed.\""
        },
        new()
        {
            Id = "lens-12",
            ContentArchetypeId = "roland-archetype",
            Pillar = "Personal",
            DisplayOrder = 3,
            Name = "Philosophy, Music, and Technical Integration",
            Description = "Jazz piano principles influencing software architecture and AI design, maintaining intellectual curiosity while managing business, balancing deep technical work with family life.",
            ExamplePosts = "Post 1: \"Learning jazz piano taught me more about agentic AI architecture than any computer science course ever did. Both require understanding harmony, improvisation within structure, and responding to unexpected changes. Here's how musical thinking makes autonomous systems more resilient.\"\n\nPost 2: \"My kids think I play video games all day because they see me coding on the couch with YouTube running in the background while they do homework. They're not entirely wrong - and here's why making your technical passion your family life changes everything about work-life integration.\""
        }
    ];

    /// <summary>
    /// Gets all story arches.
    /// </summary>
    public static IEnumerable<StoryArch> GetStoryArches() =>
    [
        new()
        {
            Id = "business-app-future",
            Name = "Business Application of the Future",
            Description = "Vision for next-generation enterprise applications powered by AI",
            Theme = "Enterprise Transformation"
        },
        new()
        {
            Id = "data-ingestion",
            Name = "Data Ingestion",
            Description = "Modern approaches to collecting and processing enterprise data",
            Theme = "Data Management"
        },
        new()
        {
            Id = "reporting-future",
            Name = "Reporting of the Future",
            Description = "AI-driven analytics and visualization for decision makers",
            Theme = "Business Intelligence"
        },
        new()
        {
            Id = "dev-process-future",
            Name = "Development Process of the Future",
            Description = "How AI transforms software development workflows",
            Theme = "Developer Experience"
        }
    ];

    /// <summary>
    /// Gets sample stories.
    /// </summary>
    public static IEnumerable<Story> GetStories() =>
    [
        new()
        {
            Id = "story-1",
            Title = "MeshWeaver: The Foundation for AI-Native Business Apps",
            Content = "In an era where enterprises struggle to integrate AI into their existing systems, MeshWeaver provides a revolutionary approach. Built on 25 years of enterprise software experience, it offers a mesh-based architecture that seamlessly connects data, workflows, and AI agents. This story explores how organizations can transition from legacy systems to AI-native applications without the traditional 18-month migration timeline.",
            StoryArchId = "business-app-future",
            AuthorId = "roland-buergi",
            Status = ContentStatus.Published,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            PublishedAt = DateTime.UtcNow.AddDays(-7)
        },
        new()
        {
            Id = "story-2",
            Title = "From Excel Hell to Data Mesh Paradise",
            Content = "Every enterprise has the same dirty secret: critical business decisions are made based on Excel spreadsheets copy-pasted between departments. This story reveals the hidden cost of this practice and presents a practical path to data mesh architecture that gives each team ownership of their data while ensuring enterprise-wide consistency and governance.",
            StoryArchId = "data-ingestion",
            AuthorId = "roland-buergi",
            Status = ContentStatus.Draft,
            CreatedAt = DateTime.UtcNow.AddDays(-14)
        },
        new()
        {
            Id = "story-3",
            Title = "When Reports Write Themselves",
            Content = "The quarterly reporting cycle consumes weeks of analyst time, only to produce reports that are outdated before they're published. This story demonstrates how agentic AI can transform reporting from a manual burden to a continuous, intelligent process that surfaces insights proactively and adapts to changing business needs in real-time.",
            StoryArchId = "reporting-future",
            AuthorId = "roland-buergi",
            Status = ContentStatus.InReview,
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        },
        new()
        {
            Id = "story-4",
            Title = "Coding with AI: A Developer's Journey",
            Content = "When I first started using AI coding assistants, I thought they were just fancy autocomplete. Today, I work with agentic AI systems that can understand entire codebases, make architectural decisions, and refactor complex systems autonomously. This is my journey from skeptic to believer, and what it means for the future of software development.",
            StoryArchId = "dev-process-future",
            AuthorId = "roland-buergi",
            Status = ContentStatus.Draft,
            CreatedAt = DateTime.UtcNow.AddDays(-3)
        }
    ];

    /// <summary>
    /// Gets sample posts.
    /// </summary>
    public static IEnumerable<Post> GetPosts() =>
    [
        new()
        {
            Id = "post-1",
            Title = "The 3-Phase AI Transformation Approach",
            Content = "Most enterprises fail at AI transformation because they try to replace everything at once. Here's the 3-phase approach I use with tier-1 companies:\n\n1. ASSESS: Map your current processes and identify high-impact, low-risk automation candidates\n2. PILOT: Deploy targeted AI agents in contained environments with clear success metrics\n3. SCALE: Expand successful pilots using the mesh architecture that connects everything\n\nThis reduces implementation risk by 80% while delivering measurable results.\n\nWhat's your biggest challenge with AI adoption?",
            StoryId = "story-1",
            Platform = "LinkedIn",
            ContentPillar = "Tactical",
            Status = ContentStatus.Published,
            PublishedAt = DateTime.UtcNow.AddDays(-5)
        },
        new()
        {
            Id = "post-2",
            Title = "Excel Hell: The Enterprise Dirty Secret",
            Content = "Your data teams are spending 80% of their time copy-pasting between Excel sheets.\n\nThis isn't just inefficient, it's dehumanizing.\n\nAfter 15 years of building data systems, I've learned that centralized data warehouses are the Tower of Babel of enterprise IT.\n\nThe solution? Data mesh architecture.\n\nHere's the framework I use to eliminate manual data work and free your team for strategic thinking:\n\n[Thread continues...]",
            StoryId = "story-2",
            Platform = "LinkedIn",
            ContentPillar = "Tactical",
            Status = ContentStatus.Draft
        }
    ];

    /// <summary>
    /// Gets sample videos.
    /// </summary>
    public static IEnumerable<Video> GetVideos() =>
    [
        new()
        {
            Id = "video-1",
            Title = "MeshWeaver Demo: AI-Native Business Applications",
            Description = "A live demonstration of MeshWeaver's capabilities, showing how enterprises can build AI-native applications that integrate seamlessly with existing systems.",
            StoryId = "story-1",
            Platform = "YouTube",
            Status = ContentStatus.Draft,
            DurationSeconds = 1200
        },
        new()
        {
            Id = "video-2",
            Title = "From Excel to Data Mesh: A Practical Guide",
            Description = "Step-by-step walkthrough of transitioning from spreadsheet-based workflows to a modern data mesh architecture.",
            StoryId = "story-2",
            Platform = "YouTube",
            Status = ContentStatus.Draft,
            DurationSeconds = 900
        }
    ];

    /// <summary>
    /// Gets sample events.
    /// </summary>
    public static IEnumerable<Event> GetEvents() =>
    [
        new()
        {
            Id = "event-1",
            Title = "Agentic AI in Enterprise: Live Demo",
            Description = "Join us for a live demonstration of how agentic AI is transforming enterprise applications. We'll show real examples from tier-1 companies and discuss practical implementation strategies.",
            StoryId = "story-1",
            EventType = "Webinar",
            Status = ContentStatus.Scheduled,
            VirtualUrl = "https://webinar.example.com/agentic-ai-demo",
            StartDate = DateTime.UtcNow.AddDays(14),
            EndDate = DateTime.UtcNow.AddDays(14).AddHours(1)
        },
        new()
        {
            Id = "event-2",
            Title = "Data Mesh Workshop",
            Description = "A hands-on workshop on implementing data mesh architecture in your organization. Learn from real-world examples and get practical guidance on avoiding common pitfalls.",
            StoryId = "story-2",
            EventType = "Workshop",
            Status = ContentStatus.Draft,
            Location = "Zurich, Switzerland",
            StartDate = DateTime.UtcNow.AddMonths(1),
            EndDate = DateTime.UtcNow.AddMonths(1).AddHours(4)
        },
        new()
        {
            Id = "event-3",
            Title = "Future of AI Development - Meetup Talk",
            Description = "A candid discussion about how AI is changing software development, from my personal journey as a developer to the implications for the entire industry.",
            StoryId = "story-4",
            EventType = "Meetup",
            Status = ContentStatus.Draft,
            Location = "Tech Hub Berlin",
            StartDate = DateTime.UtcNow.AddMonths(2),
            EndDate = DateTime.UtcNow.AddMonths(2).AddHours(2)
        }
    ];
}
