using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Sofa_API;
using Sofa_API.Endpoints;
using Sofa_API.Endpoints.DockerEndpoints.Local;
using Sofa_API.Endpoints.SecurityEndpoints;
using Sofa_API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Sofa_API.Middleware;
using Sofa_API.StatHandlers;

var builder = WebApplication.CreateBuilder(args);


builder.WebHost.ConfigureKestrel(kestrel =>
{
	kestrel.ConfigureHttpsDefaults(https =>
	{
		https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
		https.ClientCertificateValidation = (cert, _, _) =>
		{
			// Run basic certificate checks
			if (cert.IsExpiredOrTooNew()) return false;

			// Make sure the cert isn't too long-lasting. (4-month max lifespan)
			return cert.NotAfter - cert.NotBefore <= TimeSpan.FromDays(30 * 4);
		};

		https.ServerCertificate = Certificates.SofaCertificate;
		https.SslProtocols = SslProtocols.Tls12 & SslProtocols.Tls13;
	});

	Logs.LogBook.Write(new (LogStream.Info, "Network Initalization", $"Adding URL 'https://{IPAddress.Loopback}:{ApiConfig.HttpsNetworkPort}' to listen list"));
	kestrel.Listen(IPAddress.Loopback, ApiConfig.HttpsNetworkPort, listenOptions =>
	{
		listenOptions.UseHttps();
		listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
	});

	if (!ParsingMethods.IsEnvVarTrue("SOFA_LOCALHOST_ONLY"))
	{
		var localIp = StatsApi.GetLocalIpAddress();

		Logs.LogBook.Write(new (LogStream.Info, "Network Initalization", $"Adding URL 'https://{localIp}:{ApiConfig.HttpsNetworkPort}' to listen list"));
		kestrel.Listen(localIp, ApiConfig.HttpsNetworkPort, listenOptions =>
		{
			listenOptions.UseHttps();
			listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
		});
	}
});

builder.AddAuthenticationSchemes();
builder.AddAuthorizationSchemes();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, Permissions.PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, Permissions.PermissionHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, Permissions.SofaAuthorizationMessageHandler>();

Logs.LogBook.Write(new (LogStream.Notice, "Configuration Directory",
	$"Loading configuration from: '{SofaPaths.SubPaths.ConfigFilePath}'"));


Logs.LogBook.Write(new (LogStream.Notice, "Sofa Data Directory",
	$"Sofa is loading and storing its data from: '{SofaPaths.BaseDataPath}'"));


builder.Logging.ClearProviders();
var app = builder.Build();
if (ApiConfig.TerminalVerbosity > LogStream.Request || ApiConfig.ApiConfiguration.LogVerbosity > LogStream.Request)
	app.UseMiddleware<RequestLoggingMiddleware>();

if (Environment.OSVersion.Platform != PlatformID.Unix)
{
	Logs.LogBook.Write(new (LogStream.Warning, "Initialization",
		$"Detected OS is '{Environment.OSVersion.Platform}', which doesn't appear to be Unix-like. This is unsupported, here be dragons."));
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapClientSecurityEndpoints();
app.MapUserSecurityEndpoints();

app.MapSofaEndpoints();

app.MapStatsEndpoints();
app.MapConfigEndpoints();
app.MapMountsEndpoints();
app.MapWolEndpoints();
app.MapClientDataEndpoints();
app.MapPowerEndpoints();

app.MapDlContaintersEndpoints();
app.MapDlComposeEndpoints();
app.MapDlImagesEndpoints();

if (DockerLocal.IsDockerAvailable)
{
	Logs.LogBook.Write(new (LogStream.Info, "Docker", "Docker is available. Docker endpoints will be available."));

	if (DockerLocal.IsDockerComposeAvailable)
	{
		Logs.LogBook.Write(new (LogStream.Info, "Docker", "Docker-Compose is available. Docker-Compose endpoints will be available."));
	}
}
if (ParsingMethods.IsEnvVarTrue("SOFA_SKIP_FIRST_FETCH"))
{
	Logs.LogBook.Write(new (LogStream.Info, "Initialization", "Skipping first fetch cycle. This may cause the first request to be slow."));
}
else
{
	// Do a fetch cycle to let the constructors run.
	List<Task> fetchTasks = [
		Task.Run(StatsApi.CpuData.UpdateDataIfNecessary),
		Task.Run(GpuHandling.FullGpusData.UpdateDataIfNecessary),
		Task.Run(StatsApi.MemoryData.UpdateDataIfNecessary),
		Task.Run(DiskHandling.FullDisksData.UpdateDataIfNecessary),
		Task.Run(StatsApi.BatteryList.UpdateDataIfNecessary)
	];

	if (DockerLocal.IsDockerAvailable)
	{
		fetchTasks.Add(Task.Run(DockerLocal.DockerContainers.UpdateDataIfNecessary));
		fetchTasks.Add(Task.Run(DockerLocal.ImagesInfo.UpdateDataIfNecessary));
	}

	Logs.LogBook.Write(new (LogStream.Info, "Initialization", "Running an initial fetch cycle..."));
	await Task.WhenAll(fetchTasks);

	Logs.LogBook.Write(new (LogStream.Ok, "Initialization", "Fetch cycle complete."));
}
Logs.LogBook.Write(new (LogStream.Ok, "Initialization", "::: Sofa is ready :::"));

try
{
	app.Run();
}
catch (SocketException e) when (e.Message == "Cannot assign the requested address")
{
	Logs.LogBook.Write(new (LogStream.Fatal, "Network Initalization",
		"Something went wrong while binding to one of the targetted IP addresses. Make sure the targetted IP address is valid."));
}
catch (SocketException e) when (e.Message == "Permission denied")
{
	Logs.LogBook.Write(new (LogStream.Fatal, "Network Initalization",
		"The current user does not have permission to bind to one of the IP addresses or ports."));
}
catch (IOException e) when (e.InnerException is not null && e.InnerException.Message == "Address already in use")
{
	Logs.LogBook.Write(new (LogStream.Fatal, "Network Initalization",
		$"Port {ApiConfig.NetworkPort} is already in use. Another instance of Sofa may be running."));
}
finally
{
	Logs.LogBook.Write(new (LogStream.Info, "Shutdown", "Sofa is shutting down."));
	Logs.LogBook.Dispose();
}