using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace PitchGenApi.Services
{
    public class JwtService
    {
        private readonly IConfiguration _configuration;

        public JwtService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string GenerateToken(string username, int userId, string accountType, string IsDemoAccount) // Add userId parameter
        {
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]);
            var issuer = _configuration["Jwt:Issuer"];
            var audience = _configuration["Jwt:Audience"];
            var expiryMinutes = int.Parse(_configuration["Jwt:ExpiryMinutes"]); // Parse to int

            var claims = new[] {
            new Claim(ClaimTypes.Name, username), // Keep username
            new Claim("UserId", userId.ToString()), // Add UserId as a custom claim
            new Claim("UserRole", accountType.ToString()), // Add UserId as a custom claim
            new Claim ("IsDemoAccount", IsDemoAccount.ToString()),
            // ... any other claims
        };

            var credentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer,
                audience,
                claims,
                expires: DateTime.Now.AddMinutes(expiryMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        /// <summary>
        /// Written by <see cref="JwtSecurityToken"/> on every issue, so copying
        /// them across on a renewal would duplicate iss/aud and — worse — carry
        /// the old "exp" into the new token and expire it on arrival.
        /// </summary>
        private static readonly HashSet<string> ReservedClaims =
            new(StringComparer.Ordinal) { "exp", "nbf", "iat", "iss", "aud", "jti" };

        /// <summary>
        /// Re-issues a token for a caller who already holds a valid one, so an
        /// active session does not die at the ExpiryMinutes mark mid-use.
        ///
        /// Every application claim is copied across untouched: a renewed token
        /// grants exactly what the original did. This never adds rights and it
        /// cannot revive a dead session — only a request that already passed
        /// JWT validation reaches it, so the lifetime is all that moves.
        ///
        /// Claim types survive the round trip: the inbound handler maps
        /// "unique_name" to ClaimTypes.Name and the outbound one maps it back,
        /// while the custom claims (UserId, UserRole, firstname, lastname,
        /// IsDemoAccount) are passed through verbatim.
        /// </summary>
        public string RenewToken(IEnumerable<Claim> existingClaims)
        {
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]);
            var issuer = _configuration["Jwt:Issuer"];
            var audience = _configuration["Jwt:Audience"];
            var expiryMinutes = int.Parse(_configuration["Jwt:ExpiryMinutes"]);

            var carried = existingClaims
                .Where(claim => !ReservedClaims.Contains(claim.Type))
                .Select(claim => new Claim(claim.Type, claim.Value))
                .ToList();

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer,
                audience,
                carried,
                expires: DateTime.Now.AddMinutes(expiryMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public string GeneratenewToken(string username, int userId, string firstname, string lastname) // Add userId parameter
        {
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]);
            var issuer = _configuration["Jwt:Issuer"];
            var audience = _configuration["Jwt:Audience"];
            var expiryMinutes = int.Parse(_configuration["Jwt:ExpiryMinutes"]); // Parse to int

            var claims = new[] {
            new Claim(ClaimTypes.Name, username), // Keep username
            new Claim("UserId", userId.ToString()), // Add UserId as a custom claim
            new Claim("firstname", firstname.ToString()), // Add UserId as a custom claim
            new Claim ("lastname", lastname.ToString()),
            // ... any other claims
        };

            var credentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer,
                audience,
                claims,
                expires: DateTime.Now.AddMinutes(expiryMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
