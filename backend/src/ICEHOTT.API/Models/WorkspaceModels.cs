using System.ComponentModel.DataAnnotations;

namespace ICEHOTT.API.Models;

public sealed record CreateWorkspaceRequest(
    [Required, MinLength(2), MaxLength(120)] string Name);
