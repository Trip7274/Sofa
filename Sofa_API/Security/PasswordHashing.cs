using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using MessagePack;

namespace Sofa_API.Security;

public static class PasswordHashing
{
	public const byte HashLength = 32; // 256 bits
	public const byte SaltLength = 32; // 256 bits

	[MessagePackObject]
	public record struct PasswordAttributes()
	{
		[Key("P")]
		public byte Parallelism { get; } = 2;
		[Key("I")]
		public byte Iterations { get; } = 6;
		[Key("MS")]
		public ushort MemorySize { get; } = 25600; // 25 MiBs
	}

	/// <summary>
	/// Hash a new password. This randomly generates a salt and returns it along with the hashed password.
	/// </summary>
	/// <param name="inputPass">Plain-text password (1024-character limit)</param>
	/// <param name="userId">GUID of the user, used as the AssociatedData field</param>
	/// <param name="salt">This user's individual generated 64-byte salt</param>
	/// <param name="attributes"></param>
	/// <returns>A base64-encoded Argon2id string for secure storage</returns>
	/// <exception cref="ArgumentException"><c>inputPass</c> was either null, empty, or purely whitespace</exception>
	/// <exception cref="ArgumentOutOfRangeException"><c>inputPass</c> was longer than 1024 characters</exception>
	public static byte[] HashPassword(string inputPass, Guid userId, out byte[] salt, out PasswordAttributes attributes)
	{
		if (string.IsNullOrWhiteSpace(inputPass)) throw new ArgumentException("Invalid input", nameof(inputPass));
		if (inputPass.Length > 1024) throw new ArgumentOutOfRangeException(nameof(inputPass));
		attributes = new();

		salt = new byte[SaltLength];
		RandomNumberGenerator.Fill(salt);

		var argonPass = new Argon2id(Encoding.UTF8.GetBytes(inputPass))
		{
			DegreeOfParallelism = attributes.Parallelism,
			Iterations = attributes.Iterations,
			MemorySize = attributes.MemorySize,
			AssociatedData = userId.ToByteArray(),
			Salt = salt
		};

		return argonPass.GetBytes(HashLength);
	}

	///  <summary>
	/// 	Hash a password using an existing salt. This should only be used for verifying an existing user's password.
	///  </summary>
	///  <param name="inputPass">Plain-text password (1024-character limit)</param>
	///  <param name="userId">GUID of the user, used as the AssociatedData field</param>
	///  <param name="salt">This user's individual 64-byte salt</param>
	///  <param name="attributes">A <see cref="PasswordAttributes"/> object containing the settings of the original Argon2id function</param>
	///  <returns>A base64-encoded Argon2id string hashed using the user's provided salt</returns>
	///  <exception cref="ArgumentException">Either the <c>inputPass</c> argument was empty/whitespace, or the provided salt is invalid</exception>
	///  <exception cref="ArgumentOutOfRangeException">The provided <c>inputPass</c> argument was longer than 1024 characters</exception>
	public static byte[] HashPassword(string inputPass, Guid userId, byte[] salt, PasswordAttributes attributes)
	{
		if (string.IsNullOrWhiteSpace(inputPass)) throw new ArgumentException("Invalid input", nameof(inputPass));
		if (inputPass.Length > 1024) throw new ArgumentOutOfRangeException(nameof(inputPass));
		if (salt.Length != SaltLength) throw new ArgumentException("Invalid salt", nameof(salt));

		var argonPass = new Argon2id(Encoding.UTF8.GetBytes(inputPass))
		{
			DegreeOfParallelism = attributes.Parallelism,
			Iterations = attributes.Iterations,
			MemorySize = attributes.MemorySize,
			AssociatedData = userId.ToByteArray(),
			Salt = salt
		};

		return argonPass.GetBytes(HashLength);
	}
}