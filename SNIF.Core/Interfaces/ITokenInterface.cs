using SNIF.Core.Entities;
using System.Security.Claims;

namespace SNIF.Core.Interfaces
{
    public interface ITokenService
    {
        Task<string> CreateTokenAsync(User user);
        ClaimsPrincipal? ValidateToken(string token);
    }
}