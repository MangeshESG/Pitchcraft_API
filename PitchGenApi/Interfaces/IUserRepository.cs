using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface IUserRepository
    {
        Task<IEnumerable<tbl_clientdetails>> GetAllUsersAsync();
        Task<tbl_clientdetails> GetUserByIdAsync(int id);
        Task<tbl_clientdetails> GetUserByUsernameAsync(string userName);
        Task<tbl_clientdetails> AddUserAsync(tbl_clientdetails user);
        Task<tbl_clientdetails> UpdateUserAsync(tbl_clientdetails user);
        Task<bool> DeleteUserAsync(int id);

        Task<tbl_clientdetails> GetClientByIdAsync(int clientId);
        Task UpdateClientAsync(tbl_clientdetails client);
    }
}
