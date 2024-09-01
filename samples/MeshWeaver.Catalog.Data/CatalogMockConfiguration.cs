using MeshWeaver.Catalog.Domain;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Catalog.Data;

public static class CatalogMockConfiguration
{
    public static MessageHubConfiguration AddCatalogMockData(
        this MessageHubConfiguration configuration
    ) => configuration.AddData(
        data =>
            data.FromConfigurableDataSource(
                "catalog-mock",
                dataSource =>
                    dataSource
                        .WithType<MeshDocument>(type => type
                            .WithKey(instance => instance.Id).WithInitialData(MeshDocuments))
                        .WithType<MeshNode>(type => type.WithKey(x => x.Id).WithInitialData(MeshNodes))
                        .WithType<User>(type => type.WithKey(x => x.Id).WithInitialData(Users))
            )
    );

    public static IEnumerable<MeshDocument> MeshDocuments =>
[
    new("GettingStartedGuide.md", "Getting started with Northwind", "TechGuru")
        {
            Description = "A beginner's guide to using the Northwind application",
            Author = "alice",
            Thumbnail = "techblogger.jpg",
            Created = DateTime.Today,
            Tags = ["northwind", "domain-model"],
            Views = 1000,
            Likes = 500,
        },

        new("DataModelingBestPractices.md", "Best practices for data modeling", "DataModeler")
        {
            Description = "A collection of best practices for designing efficient databases",
            Author = "bob",
            Thumbnail = "datageek.jpg",
            Created = DateTime.Today,
            Tags = ["data-modeling", "database-design"],
            Views = 2000,
            Likes = 1000,
        },

        new("AIProblemSolvingTechniques.md", "AI problem-solving techniques", "AIWizard")
        {
            Description = "A guide to using AI to solve complex problems and automate tasks",
            Author = "charlie",
            Thumbnail = "aiwizard.jpg",
            Created = DateTime.Today,
            Tags = ["ai", "problem-solving"],
            Views = 1500,
            Likes = 750,
        },
        new("CodingTechniques.md", "Advanced coding techniques", "CodeCraftsman")
        {
            Description = "A collection of advanced coding techniques for improving programming skills",
            Author = "david",
            Thumbnail = "codecraftsman.png",
            Created = DateTime.Today,
            Tags = ["coding", "programming"],
            Views = 3000,
            Likes = 1500,
        },
        new("TechWisdomCollection.md", "Collection of tech wisdom", "TechSage")
        {
            Description = "A collection of tech wisdom and expert advice on various technical topics",
            Author = "emily",
            Thumbnail = "techsage.png",
            Created = DateTime.Today,
            Tags = ["tech", "wisdom"],
            Views = 2500,
            Likes = 1250,
        },
        new("DataAnalysisGuide.md", "Data analysis guide", "DataWhiz")
        {
            Description = "A comprehensive guide to exploring and analyzing data using statistical techniques",
            Author = "frank",
            Thumbnail = "datawhiz.png",
            Created = DateTime.Today,
            Tags = ["data-analysis", "statistics"],
            Views = 1800,
            Likes = 900,
        },
        new("AIEnthusiastCommunity.md", "AI enthusiast community guide", "AIEnthusiast")
        {
            Description = "A guide to the AI enthusiast community for collaboration and learning",
            Author = "grace",
            Thumbnail = "aienthusiast.png",
            Created = DateTime.Today,
            Tags = ["ai", "community"],
            Views = 2200,
            Likes = 1100,
        },
        new("TechKnowledgeRepository.md", "Tech knowledge repository", "TechGuru")
        {
            Description = "A repository of in-depth technical knowledge and insights on various topics",
            Author = "henry",
            Thumbnail = "techguru.png",
            Created = DateTime.Today,
            Tags = ["tech", "knowledge"],
            Views = 2700,
            Likes = 1350,
        },
        new("CodeNinjaAcademy.md", "Code ninja academy guide", "CodeNinja")
        {
            Description = "A guide to mastering coding skills and becoming a code ninja",
            Author = "isabella",
            Thumbnail = "codeninja.png",
            Created = DateTime.Today,
            Tags = ["coding", "ninja"],
            Views = 1900,
            Likes = 950,
        },
        new("DataGeekUniverse.md", "Data geek universe guide", "DataGeek")
        {
            Description = "A guide to the data geek universe for exploring, analyzing, and visualizing data",
            Author = "jacob",
            Thumbnail = "datageek.jpg",
            Created = DateTime.Today,
            Tags = ["data", "geek"],
            Views = 2300,
            Likes = 1150,
        },
    ];

    private static IEnumerable<MeshNode> MeshNodes =>
    [
        new("TechBlogger", "Tech Blogger Platform")
        {
            Description = "A platform for writing technical blog posts and sharing knowledge",
            Thumbnail = "techblogger.jpg",
            Followers = 5000
        },
        new("DataModeler", "Data Modeling Platform")
        {
            Description = "A platform for solving data modeling problems and designing efficient databases",
            Thumbnail = "datamodeler.jpg",
            Followers = 3000
        },
        new("AIWizard", "AI Problem Solver")
        {
            Description = "A platform for using AI to solve complex problems and automate tasks",
            Thumbnail = "aiwizard.jpg",
            Followers = 2000
        },
        new("CodeCraftsman", "Code Crafting Community")
        {
            Description = "A platform for sharing coding techniques and improving programming skills",
            Thumbnail = "codecraftsman.png",
            Followers = 4000
        },
        new("TechSage", "Tech Wisdom Repository")
        {
            Description = "A platform for sharing tech wisdom and providing expert advice",
            Thumbnail = "techsage.png",
            Followers = 2500
        },
        new("DataWhiz", "Data Analysis Playground")
        {
            Description = "A platform for exploring and analyzing data using advanced statistical techniques",
            Thumbnail = "datawhiz.png",
            Followers = 3500
        },
        new("AIEnthusiast", "AI Enthusiast Community")
        {
            Description = "A platform for AI enthusiasts to collaborate, learn, and discuss AI topics",
            Thumbnail = "aienthusiast.png",
            Followers = 4500
        },
        new("TechGuru", "Tech Knowledge Hub")
        {
            Description = "A platform for sharing in-depth technical knowledge and insights",
            Thumbnail = "techguru.png",
            Followers = 2800
        },
        new("CodeNinja", "Code Ninja Academy")
        {
            Description = "A platform for mastering coding skills and becoming a code ninja",
            Thumbnail = "codeninja.png",
            Followers = 3200
        },
        new("DataGeek", "Data Geek Universe")
        {
            Description = "A platform for data geeks to explore, analyze, and visualize data",
            Thumbnail = "datageek.jpg",
            Followers = 3800
        }
    ];

    private static IEnumerable<User> Users =>
    [
        new("alice", "alice@gmail.com", "Alice Smith")
        {
            Avatar = "av-user4.png", Bio = "Tech blogger and AI enthusiast", Followers = 1000, Following = 10
        },
        new User("bob", "bob@gmail.com", "Bob Johnson")
        {
            Avatar = "av-user1.png",
            Bio = "Software engineer and technology enthusiast",
            Followers = 2000,
            Following = 20
        },
        new User("charlie", "charlie@gmail.com", "Charlie Brown")
        {
            Avatar = "av-user2.png",
            Bio = "Web developer and open-source contributor",
            Followers = 1500,
            Following = 15
        },
        new User("david", "david@gmail.com", "David Wilson")
        {
            Avatar = "av-user3.png",
            Bio = "Data scientist and machine learning practitioner",
            Followers = 3000,
            Following = 30
        },
        new User("emily", "emily@gmail.com", "Emily Davis")
        {
            Avatar = "av-user4.png",
            Bio = "Front-end developer and UX/UI designer",
            Followers = 2500,
            Following = 25
        },
        new User("frank", "frank@gmail.com", "Frank Thompson")
        {
            Avatar = "av-user1.png",
            Bio = "Full-stack developer and technology enthusiast",
            Followers = 1800,
            Following = 18
        },
        new User("grace", "grace@gmail.com", "Grace Wilson")
        {
            Avatar = "av-user2.png",
            Bio = "Software engineer and AI researcher",
            Followers = 2200,
            Following = 22
        },
        new User("henry", "henry@gmail.com", "Henry Johnson")
        {
            Avatar = "av-user3.png",
            Bio = "Backend developer and cloud computing expert",
            Followers = 2700,
            Following = 27
        },
        new User("isabella", "isabella@gmail.com", "Isabella Martinez")
        {
            Avatar = "av-user4.png", Bio = "Mobile app developer and tech blogger", Followers = 1900, Following = 19
        },
        new User("jacob", "jacob@gmail.com", "Jacob Anderson")
        {
            Avatar = "av-user1.png",
            Bio = "Game developer and virtual reality enthusiast",
            Followers = 2300,
            Following = 23
        }
    ];
}
