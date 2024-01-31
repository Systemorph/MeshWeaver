using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Project.Contract;

public class ProjectSettings
{
    [Required]
    public string Name { get; set; }
    public string Abstract { get; set; }
    public string Thumbnail { get; set; }

    [Required]
    public string DefaultEnvironment { get; set; } 
    public bool ShouldSaveUserSettings { get; set; }
}