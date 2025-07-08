namespace PitchGenApi.Model
{
    public class EnquiryRequest
    {
        public string Prompt { get; set; }  // The main user input prompt
        public string ScrappedData { get; set; }  // Optional: Additional data (e.g., from web scraping)
        public string ModelName { get; set; } // New property for model name
    }
}
