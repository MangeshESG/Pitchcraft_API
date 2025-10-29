using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;


namespace PitchGenApi.Helpers
{
    public class EmailTemplateHelper
    {
        private readonly AppDbContext _context;
        public EmailTemplateHelper(AppDbContext context)
        {
            _context = context;
        }



        public string ReplacePlaceholders(string text, Dictionary<string, string> data)
        {
            if (string.IsNullOrEmpty(text)) return text;

            foreach (var kv in data)
            {
                var key = kv.Key;
                var value = kv.Value ?? string.Empty;

                // Replace both {key} and {{key}} patterns
                text = text.Replace("{" + key + "}", value)
                           .Replace("{{" + key + "}}", value);
            }

            return text;
        }

    }
}
