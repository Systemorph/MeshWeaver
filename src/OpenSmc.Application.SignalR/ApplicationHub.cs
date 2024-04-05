using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace OpenSmc.Application.SignalR;

public class ApplicationHub(ILogger<ApplicationHub> logger) : Hub
{
}
