using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// The FIXED list of icon names an automatic icon assignment may choose from.
///
/// <para><b>Why a fixed list.</b> A node's <c>Icon</c> may hold a Fluent icon name, but only a
/// name this assembly ships as <c>Icons/{name}.svg</c> renders on every surface: the Blazor nav
/// draws any Fluent name, while link previews, HTML cards and the browser tab can only draw a
/// shipped glyph (<see cref="MeshNodeImageHelper.ShippedIconFor"/>). A model that picks freely
/// from the whole Fluent set picks names half the product renders as nothing. Every entry here
/// is a real Microsoft Fluent UI System Icons name (20 px Regular) AND ships its tiled glyph, so
/// a model restricted to this list cannot produce an icon some surface fails to draw.</para>
///
/// <para><b>Meaning</b> is a short keyword hint for the model, not display text.</para>
///
/// <para>The list is a constant: it is built once at type initialisation and never written.
/// Adding a name means adding its <c>Icons/{lower}.svg</c> in the same change — the catalogue
/// tests fail on an entry without its glyph.</para>
/// </summary>
public static class NodeIconCatalog
{
    /// <summary>One selectable icon.</summary>
    /// <param name="Name">The Fluent UI System Icons name in PascalCase, e.g. <c>DocumentText</c> —
    /// the value written to a node's <c>Icon</c>.</param>
    /// <param name="Meaning">A short keyword hint (what the icon stands for) the model reads to choose.</param>
    public sealed record Entry(string Name, string Meaning);

    /// <summary>Every selectable icon, grouped by theme. Names are unique case-insensitively.</summary>
    public static IReadOnlyList<Entry> Entries { get; } = ImmutableArray.Create<Entry>(
        // Documents and knowledge
        new("Document", "generic document, file, page"),
        new("DocumentText", "text document, article, report, write-up"),
        new("DocumentPdf", "PDF, printable file, exported document"),
        new("Folder", "folder, collection, group of items"),
        new("Notebook", "notebook, journal, workbook, lab notes"),
        new("Book", "book, manual, handbook"),
        new("BookOpen", "reading, guide, tutorial, chapter, lesson"),
        new("Library", "library, catalogue, reference collection"),
        new("Clipboard", "clipboard, form, questionnaire, survey"),
        new("ClipboardTask", "assignment, review sheet, inspection checklist"),
        new("Note", "note, memo, short remark"),
        new("Archive", "archive, old records, retention"),
        new("Attach", "attachment, linked file, enclosure"),
        new("TaskListLtr", "task list, to-do, backlog, checklist"),

        // People and organisation
        new("Person", "person, user, individual, profile"),
        new("People", "people, group, contacts, members"),
        new("PeopleTeam", "team, department, squad, crew"),
        new("Organization", "organisation chart, hierarchy, reporting lines"),
        new("Building", "company, office, institution, premises"),
        new("ContactCard", "contact, business card, address book"),

        // Business and finance
        new("Briefcase", "business, work, job, portfolio"),
        new("Money", "money, cash, price, revenue"),
        new("Wallet", "wallet, account balance, budget"),
        new("Receipt", "receipt, invoice, bill, expense"),
        new("Calculator", "calculation, pricing model, estimate"),
        new("Savings", "savings, piggy bank, reserves"),
        new("Payment", "payment, card, transaction, billing"),
        new("Handshake", "deal, partnership, agreement, client relationship"),
        new("BuildingBank", "bank, financial institution, treasury"),
        new("DataTrending", "growth, trend, forecast, performance"),

        // Data and analytics
        new("Database", "database, data store, records, tables"),
        new("ChartMultiple", "charts, dashboard, analytics overview"),
        new("DataBarVertical", "bar chart, comparison, statistics"),
        new("DataPie", "pie chart, share, distribution, allocation"),
        new("DataLine", "line chart, time series, history"),
        new("DataHistogram", "histogram, frequency, distribution analysis"),
        new("Table", "table, spreadsheet, rows and columns"),
        new("Grid", "grid, tiles, matrix, gallery"),
        new("Filter", "filter, query, selection, criteria"),
        new("Flowchart", "flowchart, process, workflow, pipeline"),
        new("Gauge", "gauge, metric, KPI, speed"),

        // Communication
        new("Mail", "email, mail, message, inbox"),
        new("Chat", "chat, conversation, thread"),
        new("ChatMultiple", "discussion, forum, group chat"),
        new("Comment", "comment, feedback, annotation"),
        new("Call", "phone call, hotline, support line"),
        new("Megaphone", "announcement, marketing, campaign, broadcast"),
        new("Alert", "notification, alert, reminder, bell"),
        new("Send", "send, outbox, dispatch, submit"),
        new("Share", "share, publish, distribute"),
        new("News", "news, newsletter, blog, press"),

        // Calendar and time
        new("Calendar", "calendar, date, schedule, event"),
        new("Clock", "time, hours, opening times"),
        new("Timer", "timer, countdown, deadline, duration"),
        new("History", "history, log, audit trail, versions"),
        new("CalendarClock", "appointment, meeting time, booking"),
        new("Hourglass", "waiting, pending, expiry, time running"),

        // Code and development
        new("Code", "source code, script, programming"),
        new("Bug", "bug, defect, issue, incident"),
        new("Branch", "branch, version control, git"),
        new("Server", "server, infrastructure, hosting, backend"),
        new("Cloud", "cloud, online service, SaaS"),
        new("WindowConsole", "terminal, console, command line, shell"),
        new("Bot", "bot, AI agent, assistant, automation"),
        new("PuzzlePiece", "plugin, extension, module, add-on"),
        new("Apps", "apps, application catalogue, launcher"),
        new("Rocket", "launch, release, startup, deployment"),
        new("PlugConnected", "integration, connector, API connection"),
        new("BrainCircuit", "AI model, machine learning, intelligence"),
        new("Sparkle", "AI generated, magic, new, highlight"),
        new("Wand", "wizard, generator, automatic setup"),
        new("Flash", "fast, energy, power, quick action"),

        // Security
        new("Shield", "security, protection, safety, defence"),
        new("ShieldCheckmark", "verified, compliance, approved, secure"),
        new("ShieldError", "threat, vulnerability, security incident"),
        new("LockClosed", "locked, private, restricted, confidential"),
        new("Key", "key, credential, secret, access"),
        new("Fingerprint", "identity, authentication, biometrics"),
        new("Eye", "view, visibility, watch, observe"),

        // Travel and places
        new("Airplane", "travel, flight, trip, aviation"),
        new("Globe", "world, international, web, global"),
        new("Map", "map, territory, region, route"),
        new("Location", "place, address, site, location"),
        new("Home", "home, house, residence, start page"),
        new("VehicleCar", "car, motor, vehicle, fleet"),
        new("VehicleTruck", "truck, logistics, delivery, freight"),
        new("VehicleShip", "ship, marine, cargo, shipping"),
        new("VehicleBus", "bus, public transport, commute"),
        new("BuildingFactory", "factory, manufacturing, plant, production"),
        new("Earth", "earth, planet, environment, climate"),
        new("Flag", "flag, milestone, country, marker"),
        new("Bed", "hotel, accommodation, sleep, hospitality"),

        // Science and nature
        new("Beaker", "science, experiment, lab, chemistry"),
        new("Microscope", "research, analysis, biology, investigation"),
        new("LeafThree", "nature, plants, sustainability, agriculture"),
        new("WeatherSunny", "weather, sun, summer, solar"),
        new("WeatherThunderstorm", "storm, natural catastrophe, severe weather"),
        new("Fire", "fire, hazard, hot, burning"),
        new("Drop", "water, liquid, flood, drop"),
        new("AnimalDog", "animal, pet, veterinary"),

        // Education
        new("HatGraduation", "education, course, graduation, school"),
        new("Certificate", "certificate, credential, diploma, accreditation"),
        new("Lightbulb", "idea, insight, tip, innovation"),
        new("Ribbon", "award, recognition, badge, achievement"),
        new("LearningApp", "e-learning, training app, online course"),

        // Media
        new("Image", "image, picture, photo, illustration"),
        new("Video", "video, recording, film"),
        new("Headphones", "audio, podcast, music, listening"),
        new("Camera", "camera, photography, snapshot"),
        new("Mic", "microphone, voice, speech, interview"),
        new("SlideText", "presentation, slides, deck, talk"),

        // Settings and tools
        new("Settings", "settings, configuration, preferences"),
        new("Wrench", "maintenance, repair, tools, fix"),
        new("Toolbox", "toolbox, utilities, toolkit"),
        new("Options", "options, sliders, tuning, parameters"),
        new("Edit", "edit, write, draft, authoring"),
        new("Search", "search, find, lookup, discovery"),
        new("Link", "link, URL, reference, connection"),
        new("Tag", "tag, label, category, price tag"),
        new("Pin", "pinned, favourite place, important"),
        new("Star", "favourite, rating, featured"),
        new("Bookmark", "bookmark, saved item, read later"),
        new("Box", "package, product, box, container"),
        new("Cube", "3D model, component, object"),
        new("Layer", "layers, stack, levels, architecture"),
        new("Ruler", "measurement, design, specification"),

        // Insurance and risk
        new("Umbrella", "insurance, cover, protection, policy"),
        new("Warning", "warning, risk, caution, attention"),
        new("ErrorCircle", "error, failure, problem, rejection"),
        new("HeartPulse", "life and health, vitals, wellbeing"),

        // Legal
        new("Gavel", "legal, court, ruling, law"),
        new("Scales", "justice, balance, fairness, regulation"),
        new("DocumentSignature", "contract, signed document, agreement"),

        // Health
        new("Heart", "health, care, love, favourite"),
        new("Stethoscope", "medical, diagnosis, clinic, healthcare"),
        new("Pill", "medicine, pharmacy, drug, treatment"),
        new("Doctor", "doctor, physician, medical staff"),
        new("Accessibility", "accessibility, inclusion, disability"),
        new("FoodApple", "nutrition, diet, healthy food"),

        // Shopping and commerce
        new("Cart", "shopping cart, order, checkout"),
        new("ShoppingBag", "shopping, store, retail purchase"),
        new("Gift", "gift, present, voucher, reward"),
        new("BuildingRetail", "shop, retail store, branch"),

        // Goals and general
        new("Target", "goal, objective, target, focus"),
        new("Trophy", "win, success, competition, champion"),
        new("Checkmark", "done, approved, complete, yes"),
        new("CheckmarkCircle", "status OK, resolved, accepted"),
        new("Games", "games, play, entertainment, fun"),
        new("SportSoccer", "sport, football, club, match"),
        new("Translate", "translation, language, localisation"),
        new("Diamond", "premium, value, quality, gem"),
        new("Question", "question, help, FAQ, unknown"),
        new("Info", "information, about, details, overview"),
        new("Desktop", "desktop, computer, workstation"),
        new("Phone", "mobile phone, smartphone, mobile app"),
        new("Food", "food, restaurant, meal, catering")
    );

    private static readonly ImmutableHashSet<string> Names =
        Entries.Select(e => e.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="name"/> is a catalogue entry, compared case-insensitively.
    /// Null, empty and whitespace are never members.
    /// </summary>
    /// <param name="name">A candidate icon name, e.g. a model's answer.</param>
    public static bool Contains(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Names.Contains(name.Trim());
}
