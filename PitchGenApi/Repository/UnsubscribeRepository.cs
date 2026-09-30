using Microsoft.Data.SqlClient;
﻿using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

public class UnsubscribeRepository : IUnsubscribeRepository
{
    private readonly AppDbContext _context;

    public UnsubscribeRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<string> GenerateUnsubscribeLinkAsync(string companyName, int clientId, int contactId, string email)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            throw new ArgumentException("Company name is required.");

        if (clientId <= 0)
            throw new ArgumentException("ClientId is required.");

        if (contactId <= 0)
            throw new ArgumentException("ContactId is required.");

        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");

        companyName = companyName.Trim();
        email = email.Trim();

        // Company slug
        var companySlug = companyName.ToLowerInvariant();

        companySlug = Regex.Replace(
            companySlug,
            @"[^a-z0-9\s-]",
            "");

        companySlug = Regex.Replace(
            companySlug,
            @"\s+",
            "-");

        companySlug = Regex.Replace(
            companySlug,
            @"-+",
            "-");

        companySlug = companySlug.Trim('-');

        // Existing token check
        var existingToken =
            await _context.UnsubscribeTokens
                .FirstOrDefaultAsync(x =>
                    x.ClientId == clientId &&
                    x.ContactId == contactId);

        string token;
        UnsubscribeTokens? unsubscribeToken = null;

        if (existingToken != null)
        {
            token = existingToken.Token;

            existingToken.Email = email;
            existingToken.CompanyName = companyName;
            existingToken.CompanySlug = companySlug;
            existingToken.UpdatedAt = DateTime.UtcNow;

            _context.UnsubscribeTokens.Update(existingToken);
        }
        else
        {
            // Secure token
            var bytes =
                RandomNumberGenerator.GetBytes(32);

            token = Convert
                .ToHexString(bytes)
                .ToLowerInvariant();

            unsubscribeToken =
                new UnsubscribeTokens
                {
                    ClientId = clientId,
                    ContactId = contactId,
                    Email = email,
                    CompanyName = companyName,
                    CompanySlug = companySlug,
                    Token = token,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

            await _context.UnsubscribeTokens
                .AddAsync(unsubscribeToken);
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateTokenKey(ex) && existingToken == null)
        {
            // Another request created the token after our lookup.
            _context.Entry(unsubscribeToken!).State = EntityState.Detached;
            existingToken = await _context.UnsubscribeTokens.SingleAsync(x =>
                x.ClientId == clientId && x.ContactId == contactId);
            token = existingToken.Token;
        }

        var unsubscribeLink =
            $"https://link.pitchkraft.ai/{companySlug}/unsubscribe" +
            $"?token={Uri.EscapeDataString(token)}";

        return unsubscribeLink;
    }

    private static bool IsDuplicateTokenKey(DbUpdateException exception)
    {
        return exception.InnerException is SqlException sqlException
            && (sqlException.Number == 2601 || sqlException.Number == 2627);
    }

    public async Task<string> GenerateOneClickUnsubscribeLinkAsync(string companyName, int clientId, int contactId, string email)
    {
        var tokenData = await _context.UnsubscribeTokens
            .FirstOrDefaultAsync(x =>
                x.ClientId == clientId &&
                x.ContactId == contactId);

        string token;

        if (tokenData != null)
        {
            token = tokenData.Token;
        }
        else
        {
            var bytes = RandomNumberGenerator.GetBytes(32);

            token = Convert
                .ToHexString(bytes)
                .ToLowerInvariant();

            var newToken = new UnsubscribeTokens
            {
                ClientId = clientId,
                ContactId = contactId,
                Email = email,
                CompanyName = companyName,
                Token = token,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            await _context.UnsubscribeTokens.AddAsync(newToken);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsDuplicateTokenKey(ex))
            {
                // Another request created the token after our lookup.
                _context.Entry(newToken).State = EntityState.Detached;
                tokenData = await _context.UnsubscribeTokens.SingleAsync(x =>
                    x.ClientId == clientId && x.ContactId == contactId);
                token = tokenData.Token;
            }
        }

        return
            $"https://www.app.pitchkraft.ai/api/crm/OneClick" +
            $"?token={Uri.EscapeDataString(token)}";
    }
}
