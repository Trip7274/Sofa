using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MessagePack;

namespace Sofa_API.Security;

public abstract class HasPermissions : IEquatable<HasPermissions>
{
	protected HasPermissions(string name, Guid guid, Dictionary<string, List<string>> permissions, bool isPaused)
	{
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty", nameof(name));
		if (name.Length > 128) throw new ArgumentException("Name is too long", nameof(name));

		Name = ParsingMethods.SanitizeString(name);
		PermissionList = permissions;
		Guid = guid;
		IsPaused = isPaused;
	}

	public Dictionary<string, List<string>> PermissionList { get; protected set; }
	public Guid Guid { get; }
	public string Name { get; protected set; }
	public bool IsPaused { get; protected set; }
	public virtual bool IsActive { get; protected set; }

	[IgnoreMember]
	public string NameSlug
	{
		get
		{
			field ??= ParsingMethods.ConvertTextToSlug(Name);
			return field;
		}
	}


	public bool HasPermission(Permissions.SofaPermission permissionRequirement)
	{
		if (IsPaused || !IsActive) return false;
		if (PermissionList.ContainsKey("admin")) return true;

		// Extract the user's relevant permission as a SofaPermission object, if found.
		if (PermissionList.Count == 0 || !PermissionList.TryGetValue(permissionRequirement.PermissionString, out var userPermissionPowers)) return false;
		var userPermission = new Permissions.SofaPermission(permissionRequirement.PermissionString, userPermissionPowers);

		return userPermission.Allows(permissionRequirement.PermPowers);
	}
	public void AddPermission(Permissions.SofaPermission permission)
	{
		if (PermissionList.TryGetValue(permission.PermissionString, out var list))
		{
			list.AddRange(permission.PermPowers.Where(p => !list.Contains(p)));
			PermissionList[permission.PermissionString] = list;
		}
		else
		{
			PermissionList.Add(permission.PermissionString, permission.PermPowers);
		}
	}
	public void RemovePermission(Permissions.SofaPermission permission)
	{
		if (!PermissionList.TryGetValue(permission.PermissionString, out var list)) return;

		list.RemoveAll(p => permission.PermPowers.Contains(p));
		if (list.Count == 0) PermissionList.Remove(permission.PermissionString);
		else PermissionList[permission.PermissionString] = list;
	}
	public void SetPermissions(Dictionary<string, List<string>> permissions)
	{
		PermissionList = permissions;
	}


	public static HasPermissions? FetchRequester(Guid identifier)
	{
		if (Clients.TryFetchValidClient(identifier, out var client))
		{
			return client;
		}

		return Users.FetchUser(identifier);
	}
	public static bool TryFetchRequester([NotNullWhen(true)] Guid? identifier, [NotNullWhen(true)] out HasPermissions? requester)
	{
		if (identifier is null)
		{
			requester = null;
			return false;
		}
		requester = FetchRequester(identifier.Value);
		return requester is not null;
	}


	public void Pause()
	{
		IsPaused = true;
		SaveObject();
	}
	public void Unpause()
	{
		IsPaused = false;
		SaveObject();
	}
	private void SaveObject()
	{
		if (GetType() == typeof(User))
		{
			SecurityStores.UserStores.SaveUser((User) this);
		}
		else if (GetType() == typeof(Client))
		{
			SecurityStores.ClientStores.SaveClient((Client) this);
		}
		else
		{
			throw new InvalidOperationException("This method is not supported for this type of object.");
		}
	}


	public static bool operator ==(HasPermissions? left, HasPermissions? right)
	{
		if (left is null && right is null) return true;
		if (left is null || right is null) return false;


		return ReferenceEquals(left, right) || left.GetHashCode() == right.GetHashCode();
	}
	public static bool operator !=(HasPermissions? left, HasPermissions? right) => !(left == right);
	public bool Equals([NotNullWhen(true)] HasPermissions? other)
	{
		if (other is null) return false;

		return this == other;
	}
	public override bool Equals([NotNullWhen(true)] object? obj)
	{
		return obj is HasPermissions hasPermissionsObj && Equals(hasPermissionsObj);
	}
	public override int GetHashCode() => HashCode.Combine(GetType(), Guid);
}

[MessagePackObject(true, AllowPrivate = true)]
public sealed partial class User : HasPermissions
{
	[SerializationConstructor]
	private User (string name, string? profilePictureUrl, Guid guid, byte[] password, byte[] salt,
		PasswordHashing.PasswordAttributes attributes, Dictionary<string, List<string>> permissionList, bool isPaused, DecorationalProperties decorationalProps)
		: base(name, guid, permissionList, isPaused, decorationalProps)
	{
		if (password.Length != PasswordHashing.HashLength) throw new ArgumentException("Password is invalid.", nameof(password));
		if (salt.Length != PasswordHashing.SaltLength) throw new ArgumentException("Salt is invalid", nameof(salt));

		// Checks done

		ProfilePictureUrl = profilePictureUrl;
		Password = password;
		Salt = salt;
		Attributes = attributes;
	}
	public User(string username, string password, string? profilePictureUrl,
		Dictionary<string, List<string>>? permissions = null, bool isPaused = false)
		: base(username, Guid.NewGuid(), permissions ?? [], isPaused)
	{
		if (Users.DoesUserExist(username)) throw new ArgumentException("Username already exists", nameof(username));
		if (Users.DoesUserSlugExist(ParsingMethods.ConvertTextToSlug(username))) throw new ArgumentException("A similar username already exists", nameof(username));
		if (password.Length > 1024) throw new ArgumentException("Password too long", nameof(password));

		profilePictureUrl = ParsingMethods.SanitizeString(profilePictureUrl);
		if (profilePictureUrl is not null &&
		    (string.IsNullOrWhiteSpace(profilePictureUrl) ||
		    !profilePictureUrl.StartsWith("https://"))) throw new ArgumentException("Invalid profile picture URL", nameof(profilePictureUrl));

		password = ParsingMethods.SanitizeString(password);

		ProfilePictureUrl = profilePictureUrl;
		var hashedPassword = PasswordHashing.HashPassword(password, Guid, out var salt, out var attributes);
		Password = hashedPassword;
		Salt = salt;
		Attributes = attributes;
	}

	/// <summary>
	/// A publicly accessible URL to the user's profile picture.
	/// </summary>
	public string? ProfilePictureUrl { get; set; }
	/// <summary>
	/// The user's hashed password.
	/// </summary>
	public byte[] Password { get; private set; }
	public byte[] Salt { get; private set; }
	public PasswordHashing.PasswordAttributes Attributes { get; private set; }
	public override bool IsActive => true;


	public bool Edit(Dictionary<string, string?> changes)
	{
		var changesMade = false;
		foreach (var (key, value) in changes)
		{
			switch (key)
			{
				case nameof(Name):
				{
					if (string.IsNullOrWhiteSpace(value) || Name == value)
						continue;

					Name = ParsingMethods.SanitizeString(value);
					break;
				}
				case nameof(ProfilePictureUrl):
				{
					if (value is null)
					{
						ProfilePictureUrl = null;
						break;
					}

					if (ProfilePictureUrl == value || !value.StartsWith("https://"))
						continue;

					ProfilePictureUrl = ParsingMethods.SanitizeString(value);
					break;
				}
				case nameof(Password):
				{
					if (string.IsNullOrWhiteSpace(value))
						continue;

					var hashedPassword = PasswordHashing.HashPassword(value, Guid, out var salt, out var attributes);
					Password = hashedPassword;
					Salt = salt;
					Attributes = attributes;
					break;
				}
				case nameof(IsPaused):
				{
					if (value is null || !bool.TryParse(value, out var result) || IsPaused == result) continue;
					IsPaused = result;
					break;
				}

				default:
				{
					throw new ArgumentException("Invalid key", key);
				}
			}

			changesMade = true;
		}
		if (changesMade) SecurityStores.UserStores.SaveUser(this);
		return changesMade;
	}

	public Dictionary<string, dynamic?> ToDictionary()
	{
		return new()
		{
			{ nameof(Name), Name },
			{ nameof(ProfilePictureUrl), ProfilePictureUrl },
			{ nameof(Guid), Guid },
			{ nameof(IsPaused), IsPaused },
			{ nameof(PermissionList), PermissionList }
		};
	}
}
[MessagePackObject(true, AllowPrivate = true)]
public sealed partial class Client : HasPermissions
{
	[SerializationConstructor]
	private Client(string name, string thumbprint, Guid guid, Dictionary<string, List<string>> permissionList,
		bool isPaused, bool hasAcknowledgedFutureCert,
		ClientPermissionRequest? pendingPermissionRequest, bool isActive) : base(name, guid, permissionList, isPaused)
	{
		if (thumbprint.Length != 64) throw new ArgumentException("Invalid thumbprint", nameof(thumbprint));

		// Checks done

		Thumbprint = thumbprint;
		IsActive = isActive;
		HasAcknowledgedFutureCert = hasAcknowledgedFutureCert;
		PendingPermissionRequest = pendingPermissionRequest;

		if (pendingPermissionRequest is not null)
		{
			if (pendingPermissionRequest.Expired)
			{
				ClearRequestedPermissions();
			}
			else
			{
				_ = Task.Run(() => Task.Delay(pendingPermissionRequest.ExpirationTime - DateTime.UtcNow).ContinueWith(_ => ClearRequestedPermissions()));
			}
		}
	}
	public Client (string clientName, string thumbprint, Dictionary<string, List<string>>? permissionList = null,
		bool isPaused = false, bool isActive = false) : base(clientName, Guid.NewGuid(), permissionList ?? [], isPaused)
	{
		if (Clients.DoesClientSlugExist(ParsingMethods.ConvertTextToSlug(clientName)))
			throw new ArgumentException("Client name already exists", nameof(clientName));

		if (thumbprint.Length != 64) throw new ArgumentException("Invalid thumbprint", nameof(thumbprint));

		// Checks done

		Thumbprint = thumbprint;
		IsActive = isActive;
	}
	static Client()
	{
		var encoderSettings = new TextEncoderSettings(UnicodeRanges.BasicLatin);
		encoderSettings.AllowCharacters('+');
		CprJsonSerializerOptions = new()
		{
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			Encoder = JavaScriptEncoder.Create(encoderSettings)
		};
	}

	public string Thumbprint { get; private set; }
	public override bool IsActive { get; protected set; }
	public bool HasAcknowledgedFutureCert { get; set; }


	private static readonly JsonSerializerOptions CprJsonSerializerOptions;
	public ClientPermissionRequest? PendingPermissionRequest { get; private set; }

	public void SetPermissionRequest(ClientPermissionRequest request)
	{
		string directoryPath = Path.Combine(SecurityStores.BaseSecurityPath, "Requests", "permissionUpdates");
		string filePath = Path.Combine(directoryPath, $"{NameSlug}.json");

		Directory.CreateDirectory(directoryPath);
		File.Delete(filePath);

		Dictionary<string, dynamic> cprDict = new()
		{
			["RequesterName"] = Name,
			["ID"] = Guid,
			["CurrentPermissions"] = PermissionList.ToDictionary(
				entry => entry.Key,
				entry => string.Join(',', entry.Value)),
			["RequestedPermissions"] = request.RequestedPermissionChanges,
			["TimeRequested"] = request.TimeRequested,
			["ExpirationTime"] = request.ExpirationTime,
			["RequestKey"] = Convert.ToBase64String(request.PermissionRequestKey)
		};

		PendingPermissionRequest = request;
		File.WriteAllText(filePath, JsonSerializer.Serialize(cprDict, CprJsonSerializerOptions));
		_ = Task.Run(() => Task.Delay(ApiConfig.ApiConfiguration.ClientRequestLifetime).ContinueWith(_ => ClearRequestedPermissions()));

		SecurityStores.ClientStores.SaveClient(this);
	}
	public void ApplyRequestedPermissions(byte[] requestKey)
	{
		if (PendingPermissionRequest is null)
		{
			throw new InvalidOperationException("Permission request not set");
		}
		if (PendingPermissionRequest.Expired)
		{
			ClearRequestedPermissions();
			throw new InvalidOperationException("Permission request expired");
		}
		if (!PendingPermissionRequest.PermissionRequestKey.SequenceEqual(requestKey))
		{
			throw new InvalidOperationException("Permission request key does not match");
		}

		foreach (var requestedPerm in PendingPermissionRequest.RequestedPermissionChanges)
		{
			switch (requestedPerm.Key[0])
			{
				case '+':
				{
					if (!Permissions.SofaPermission.TryParse(requestedPerm.Key[1..], out var parsedPerm))
					{
						throw new ArgumentException("List contained an invalid permission request", requestedPerm.Key);
					}
					AddPermission(parsedPerm);
					break;
				}
				case '-':
				{
					if (!Permissions.SofaPermission.TryParse(requestedPerm.Key[1..], out var parsedPerm))
					{
						throw new ArgumentException("List contained an invalid permission request", requestedPerm.Key);
					}
					RemovePermission(parsedPerm);
					break;
				}
			}
		}

		ClearRequestedPermissions();
	}
	public void ClearRequestedPermissions()
	{
		var filePath =
			Path.Combine(SecurityStores.BaseSecurityPath, "Requests", "permissionUpdates", $"{NameSlug}.json");

		PendingPermissionRequest = null;
		if (File.Exists(filePath))
		{
			File.Delete(filePath);
		}
		SecurityStores.ClientStores.SaveClient(this);
	}


	[IgnoreMember]
	public bool CanRegister =>
		IsUsable && HasPermission(new Permissions.SofaPermission("client-management", ["register"]));
	[IgnoreMember]
	public bool IsUsable => IsActive && !IsPaused;


	public bool Edit(Dictionary<string, string?> changes)
	{
		var changesMade = false;
		foreach (var (key, value) in changes)
		{
			switch (key)
			{
				case nameof(Name):
				{
					if (string.IsNullOrWhiteSpace(value) || Name == value) continue;
					Name = ParsingMethods.SanitizeString(value);
					break;
				}

				default:
				{
					throw new ArgumentException("Invalid key", key);
				}
			}

			changesMade = true;
		}
		if (changesMade) SecurityStores.ClientStores.SaveClient(this);
		return changesMade;
	}
	public bool RefreshCertificate(X509Certificate2 newCertificate, [NotNullWhen(false)] out string? errorMessage)
	{
		errorMessage = null;
		// Run basic certificate checks
		if (newCertificate.IsExpiredOrTooNew())
		{
			errorMessage = "Certificate is not valid yet or has expired";
			return false;
		}

		// Make sure the cert isn't too long-lasting. (4-month max lifespan)
		if (newCertificate.NotAfter - newCertificate.NotBefore > TimeSpan.FromDays(30 * 4))
		{
			errorMessage = "Certificate is too long-lasting. The maximum allowed lifespan is 4 months.";
			return false;
		}

		Thumbprint = newCertificate.GetCertHashString(HashAlgorithmName.SHA256);
		SecurityStores.ClientStores.SaveClient(this);
		Clients.RefreshList();
		return true;
	}
	public void Activate()
	{
		IsActive = true;
		SecurityStores.ClientStores.SaveClient(this);
	}
	public void Delete(bool deleteFiles = true)
	{
		IsPaused = true;
		IsActive = false;
		Clients.RemoveClient(this, !deleteFiles);
	}
	public Dictionary<string, dynamic> ToDictionary()
	{
		return new()
		{
			{ nameof(Name), Name },
			{ nameof(Guid), Guid },
			{ nameof(Thumbprint), Thumbprint },
			{ nameof(IsActive), IsActive },
			{ nameof(IsPaused), IsPaused },
			{ nameof(PermissionList), PermissionList }
		};
	}
}

[MessagePackObject(true, AllowPrivate = true)]
public sealed partial record ClientPermissionRequest
{
	[SerializationConstructor]
	private ClientPermissionRequest(byte[] permissionRequestKey, Dictionary<string, string?> requestedPermissionChanges, DateTime timeRequested)
	{
		RequestedPermissionChanges = requestedPermissionChanges;
		PermissionRequestKey = permissionRequestKey;
		TimeRequested = timeRequested;
	}
	public ClientPermissionRequest(Dictionary<string, string?> requestedPermissionChanges)
	{
		if (requestedPermissionChanges.Any(perm => perm.Key[0] is not ('+' or '-') && perm.Key.Length < 4))
		{
			throw new ArgumentException("List contained an incorrectly formatted permission.", nameof(RequestedPermissionChanges));
		}

		RequestedPermissionChanges = requestedPermissionChanges;
		PermissionRequestKey = RandomNumberGenerator.GetBytes(32);
		TimeRequested = DateTime.UtcNow;
	}

	public byte[] PermissionRequestKey { get; }
	public Dictionary<string, string?> RequestedPermissionChanges { get; }
	public DateTime TimeRequested { get; }
	public DateTime ExpirationTime => TimeRequested + ApiConfig.ApiConfiguration.ClientRequestLifetime;
	public bool Expired => DateTime.UtcNow > ExpirationTime;

	public Dictionary<string, dynamic?> ToDictionary(bool includeKey = false)
	{
		return new()
			{
				{ nameof(PermissionRequestKey), includeKey ? Convert.ToBase64String(PermissionRequestKey) : null },
				{ nameof(RequestedPermissionChanges), RequestedPermissionChanges },
				{ nameof(TimeRequested), TimeRequested },
				{ nameof(ExpirationTime), ExpirationTime }
			};
	}
}


public static class Clients
{
	static Clients()
	{
		foreach (var client in SecurityStores.ClientStores.GetAllClients())
		{
			AvailableClients.Add(client.Guid, client);
			AvailableClientThumbs.Add(client.Thumbprint);
			AvailableClientSlugs.Add(client.NameSlug);
		}

		// Load previous registration requests, if any.

		string requestDirPath = Path.Combine(SecurityStores.BaseSecurityPath, "Requests", "registrationRequests");
		if (!Directory.Exists(requestDirPath))
		{
			Directory.CreateDirectory(requestDirPath);
			return;
		}

		foreach (var checkedFilePath in Directory.EnumerateFiles(requestDirPath, "*", SearchOption.AllDirectories))
		{
			// As a preliminary check, make sure the file at least claims to be a JSON file, and delete previously failed-to-be-parsed entries.
			if (checkedFilePath.EndsWith(".json.failed") || !checkedFilePath.EndsWith(".json"))
			{
				File.Delete(checkedFilePath);
				continue;
			}

			try
			{
				var parsedJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(checkedFilePath));
				if (parsedJson is null)
				{
					throw new JsonException("Invalid JSON");
				}

				// Check if the request is too outdated and corresponds to an existing client.
				var timeUntilExpiration = parsedJson["ExpirationTime"].GetDateTime().ToUniversalTime() - DateTime.UtcNow;
				if (!TryFetchValidClient(parsedJson["ID"].GetGuid(), out var client, true, true) || timeUntilExpiration < TimeSpan.Zero)
				{
					// If the client's request expired, delete its object from the stores as well as the JSON file.
					if (client is not null && !client.IsActive)
					{
						RemoveClient(client, false);
					}
					File.Delete(checkedFilePath);
					continue;
				}

				string registrationKey = parsedJson["RegistrationKey"].GetString()!;

				AddPendingClient(registrationKey, client, timeUntilExpiration);
			}
			catch (Exception e)
			{
				Logs.LogBook.Write(new (LogStream.Error, "Pending Client Restore", $"Error while restoring client registration request {checkedFilePath}: {e.Message}. This JSON will be deleted on the next restart."));
				File.Move(checkedFilePath, checkedFilePath + ".failed");
			}
		}
	}
	private static readonly Dictionary<Guid, Client> AvailableClients = [];
	private static readonly HashSet<string> AvailableClientThumbs = [];
	private static readonly HashSet<string> AvailableClientSlugs = [];


	public static Dictionary<string, Client> PendingClients { get; } = [];
	public static void AddPendingClient(string registrationKey, Client client, TimeSpan? ttl = null)
	{
		ttl ??= ApiConfig.ApiConfiguration.ClientRequestLifetime;

		PendingClients.Add(registrationKey, client);
		_ = Task.Run(() => Task.Delay(ttl.Value).ContinueWith(_ => RemovePendingClient(registrationKey, client)));
	}
	public static void RemovePendingClient(string registrationKey, Client client)
	{
		PendingClients.Remove(registrationKey);
		if (!client.IsActive) RemoveClient(client, false);

		string requestFilePath = Path.Combine(SecurityStores.BaseSecurityPath, "Requests", "registrationRequests", $"{client.NameSlug}.json");
		if (File.Exists(requestFilePath))
		{
			File.Delete(requestFilePath);
		}
	}


	public static List<Dictionary<string, dynamic>> FetchAllClients() =>
		AvailableClients.Select(client => client.Value.ToDictionary()).ToList();
	public static int Count => AvailableClients.Count;

	public static IEnumerable<Client> GetMasterClients => AvailableClients.Values.Where(client => client.CanRegister);

	public static bool DoesClientExist(string thumbprint) => AvailableClientThumbs.Contains(thumbprint);
	public static bool DoesClientSlugExist(string slug) => AvailableClientSlugs.Contains(slug);
	public static bool DoesClientExist(Guid guid) => AvailableClients.ContainsKey(guid);

	public static Client? FetchValidClient(string thumbprint)
	{
		if (!DoesClientExist(thumbprint)) return null;

		return AvailableClients.FirstOrDefault(client => client.Value.Thumbprint == thumbprint).Value;
	}

	/// <summary>
	/// Attempts to retrieve a valid client object with the provided client's certificate thumbprint.
	/// </summary>
	/// <param name="thumbprint">The SHA-256 thumbprint of the client's certificate.</param>
	/// <param name="client">The client object, if found.</param>
	/// <param name="allowPaused">Whether to count paused clients as valid. Defaults to false.</param>
	/// <param name="allowInactive">Whether to count inactive clients as valid. Defaults to false.</param>
	/// <returns>True if a valid client object is found.</returns>
	/// <remarks>
	/// Do note that the <c cref="client">client</c> parameter might be set, while the method still returned false. This means that, while a client was found, it was set to be paused. <br/><br/>
	/// This method is less efficient than <see cref="TryFetchValidClient(Guid?, out Client?, bool, bool)"/>, as it has to iterate through all clients, thus it should only be used when the other method is inapplicable.
	/// </remarks>
	public static bool TryFetchValidClient([NotNullWhen(true)] string? thumbprint, [NotNullWhen(true)] out Client? client, bool allowPaused = false, bool allowInactive = false)
	{
		if (string.IsNullOrWhiteSpace(thumbprint) || !DoesClientExist(thumbprint))
		{
			client = null;
			return false;
		}

		client = AvailableClients.FirstOrDefault(client => client.Value.Thumbprint == thumbprint).Value;
		return client is not null && (!client.IsPaused || allowPaused) && (client.IsActive || allowInactive);
	}

	///   <summary>
	///   Attempts to retrieve a valid client object with the provided client's GUID.
	///   </summary>
	///   <param name="guid">The GUID of the client to retrieve.</param>
	///   <param name="client">The client object, if found.</param>
	///   <param name="allowPaused">Whether to count paused clients as valid. Defaults to false.</param>
	///   <param name="allowInactive">Whether to count Inactive clients as calid. Defaults to false.</param>
	///   <returns>True if a valid client object is found and is not paused (or paused, if <c>allowPaused</c> is set).</returns>
	///   <remarks>
	/// 	 Do note that the <c cref="client">client</c> parameter might be set, while the method still returned false. This means that, while a client was found, it was set to be paused. <br/><br/>
	///   This method is more efficient than <see cref="TryFetchValidClient(string?, out Client?, bool, bool)"/>, as it does not have to iterate through all clients, thus it should be used whenever possible.
	///   </remarks>
	public static bool TryFetchValidClient([NotNullWhen(true)] Guid? guid, [NotNullWhen(true)] out Client? client, bool allowPaused = false, bool allowInactive = false)
	{
		if (guid is null || !AvailableClients.TryGetValue(guid.Value, out client))
		{
			client = null;
			return false;
		}

		return (!client.IsPaused || allowPaused) && (client.IsActive || allowInactive);
	}

	public static void RefreshList()
	{
		AvailableClients.Clear();
		AvailableClientThumbs.Clear();
		AvailableClientSlugs.Clear();

		foreach (var client in SecurityStores.ClientStores.GetAllClients())
		{
			AvailableClients.Add(client.Guid, client);
			AvailableClientThumbs.Add(client.Thumbprint);
			AvailableClientSlugs.Add(client.NameSlug);
		}
	}

	public static void AddClient(Client client)
	{
		SecurityStores.ClientStores.SaveClient(client);

		AvailableClientThumbs.Add(client.Thumbprint);
		AvailableClientSlugs.Add(client.NameSlug);
		AvailableClients.Add(client.Guid, client);
	}
	public static void RemoveClient(Client client, bool keepOtherStoreData)
	{
		SecurityStores.ClientStores.RemoveClient(client, keepOtherStoreData);
		AvailableClientThumbs.Remove(client.Thumbprint);
		AvailableClientSlugs.Remove(client.NameSlug);
		AvailableClients.Remove(client.Guid);
	}
}
public static class Users
{
	static Users()
	{
		foreach (var user in SecurityStores.UserStores.GetAllUsers())
		{
			AvailableUsers.Add(user.Guid, user);
			AvailableUsernames.Add(user.Name);
			AvailableUsernameSlugs.Add(user.NameSlug);
		}
	}
	private static readonly Dictionary<Guid, User> AvailableUsers = [];
	private static readonly HashSet<string> AvailableUsernames = [];
	private static readonly HashSet<string> AvailableUsernameSlugs = [];

	public static List<Dictionary<string, dynamic?>> FetchAllUsers() =>
		AvailableUsers.Select(user => user.Value.ToDictionary()).ToList();
	public static int Count => AvailableUsers.Count;

	public static bool DoesUserExist(Guid userId) => AvailableUsers.ContainsKey(userId);
	public static bool DoesUserExist(string username) => AvailableUsernames.Contains(ParsingMethods.SanitizeString(username));
	public static bool DoesUserSlugExist(string slug) => AvailableUsernameSlugs.Contains(slug);

	public static User? FetchUser(string username)
	{
		return TryFetchUser(username, out var user) ? user : null;
	}
	public static User? FetchUser(Guid guid)
	{
		return TryFetchUser(guid, out var user) ? user : null;
	}
	public static bool TryFetchUser([NotNullWhen(true)] string? username, [NotNullWhen(true)] out User? user)
	{
		if (string.IsNullOrWhiteSpace(username) || !DoesUserExist(username))
		{
			user = null;
			return false;
		}
		username = ParsingMethods.SanitizeString(username);

		user = AvailableUsers.FirstOrDefault(user => user.Value.Name == username).Value;
		return user is not null && !user.IsPaused;
	}
	public static bool TryFetchUser([NotNullWhen(true)] Guid? guid, [NotNullWhen(true)] out User? user)
	{
		if (guid is null || !DoesUserExist(guid.Value))
		{
			user = null;
			return false;
		}

		user = AvailableUsers.GetValueOrDefault(guid.Value);
		return user is not null && !user.IsPaused;
	}

	public static bool AuthenticateUser(string username, string presentedPassword, [NotNullWhen(true)] out User? user)
	{
		username = ParsingMethods.SanitizeString(username);
		presentedPassword = ParsingMethods.SanitizeString(presentedPassword);

		if (TryFetchUser(username, out var foundUser) && !foundUser.IsPaused &&
		    foundUser.Password.SequenceEqual(PasswordHashing.HashPassword(presentedPassword, foundUser.Guid, foundUser.Salt, foundUser.Attributes)))
		{
			user = foundUser;
			return true;
		}
		user = null;
		return false;
	}

	public static void RefreshList()
	{
		AvailableUsers.Clear();
		AvailableUsernames.Clear();
		AvailableUsernameSlugs.Clear();

		foreach (var user in SecurityStores.UserStores.GetAllUsers())
		{
			AvailableUsers.Add(user.Guid, user);
			AvailableUsernames.Add(user.Name);
			AvailableUsernameSlugs.Add(user.NameSlug);
		}
	}

	public static void AddUser(User user)
	{
		SecurityStores.UserStores.SaveUser(user);

		AvailableUsers.Add(user.Guid, user);
		AvailableUsernames.Add(user.Name);
		AvailableUsernameSlugs.Add(user.NameSlug);
	}
	public static void RemoveUser(User user, bool keepOtherStoreData)
	{
		SecurityStores.UserStores.RemoveUser(user, keepOtherStoreData);

		AvailableUsers.Remove(user.Guid);
		AvailableUsernames.Remove(user.Name);
		AvailableUsernameSlugs.Remove(user.NameSlug);
	}
}