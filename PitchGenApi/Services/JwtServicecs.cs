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
    }
}
