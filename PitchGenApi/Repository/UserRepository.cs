using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PitchGenApi.Repository;
public class UserRepository : IUserRepository
{
    private readonly AppDbContext _context;

    public UserRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<tbl_clientdetails>> GetAllUsersAsync()
    {
        try
        {

       
        return await _context.tbl_clientdetails.ToListAsync();
        }
        catch (Exception ex)
        {

            throw;
        }
    }

    public async Task<tbl_clientdetails> GetUserByIdAsync(int id)
    {
        return await _context.tbl_clientdetails.FindAsync(id);
    }
    public async Task<tbl_clientdetails?> GetUserByUsernameAsync(string username)
    {
        return await _context.tbl_clientdetails.FirstOrDefaultAsync(u => u.UserName == username);
    }

    public async Task<tbl_clientdetails> AddUserAsync(tbl_clientdetails user)
    {
        _context.tbl_clientdetails.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    public async Task<tbl_clientdetails> UpdateUserAsync(tbl_clientdetails user)
    {
        _context.tbl_clientdetails.Update(user);
        await _context.SaveChangesAsync();
        return user;
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        var user = await _context.tbl_clientdetails.FindAsync(id);
        if (user == null) return false;

        _context.tbl_clientdetails.Remove(user);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<tbl_clientdetails> GetClientByIdAsync(int clientId)
    {
        return await _context.tbl_clientdetails.FindAsync(clientId);
    }

    public async Task UpdateClientAsync(tbl_clientdetails client)
    {
        _context.tbl_clientdetails.Update(client);
        await _context.SaveChangesAsync();
    }
}
