using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Services;
using UglyToad.PdfPig;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly JwtService _jwtService;
        private readonly IPromptRepository _promptRepository;
        private readonly IPitchService _pitchservice;
        private readonly IPitchGenDataRepository _pitchgenDataRepository;
        private readonly AppDbContext _context;
        private readonly ZohoService _zohoService;
        private readonly ILogger<AuthController> _logger; // Add ILogger field



        public AuthController(IUserRepository userRepository, JwtService jwtService, IPromptRepository promptRepository, IPitchService pitchservice, IPitchGenDataRepository pitchgenDataRepository, AppDbContext context, ZohoService zohoService, ILogger<AuthController> logger)
        {
            _userRepository = userRepository;
            _jwtService = jwtService;
            _promptRepository = promptRepository;
            _pitchservice = pitchservice;
            _pitchgenDataRepository = pitchgenDataRepository;
            _context = context;
            _zohoService = zohoService;
            _logger = logger; // Assign the injected logger to the field

        }




        [HttpPost("login")]
        public async Task<IActionResult> Login([FromForm] string username, [FromForm] string password)
        {
            var user = await _userRepository.GetUserByUsernameAsync(username);
            if (user == null || !VerifyPassword(password, user.Password))
            {
                return Unauthorized(new { Message = "Invalid credentials" });
            }

            var token = _jwtService.GenerateToken(username, user.ClientID, user.AccountType.ToString(), user.IsDemoAccount.ToString());

            return Ok(new
            {
                Token = token,
                ClientID = user.ClientID,
                Isadmin = user.IsAdmin,
                IsDemoAccount = user.IsDemoAccount,
                FirstName = user.FirstName,
                LastName = user.LastName,
                CompanyName = user.CompanyName,
            });
        }


        [HttpGet("allUserDetails")]
        public async Task<IActionResult> GetAllUserDetails()
        {
            var users = await _userRepository.GetAllUsersAsync();

            var userDetails = users.Select(u => new
            {
                FirstName = u.FirstName,
                LastName = u.LastName,
                // Assuming ClientID is equivalent to Id in your User model
                ClientID = u.ClientID,
                CompanyName = u.CompanyName

            }).ToList();

            // Sort the prompts by FirstName in ascending order
            var sorteduserDetails = userDetails.OrderBy(p => p.FirstName).ToList();

            return Ok(sorteduserDetails);
        }


        [HttpGet("getDemoAccountStatus/{clientId}")]
        public async Task<IActionResult> GetDemoAccountStatus(int clientId)
        {
            try
            {
                // Get the client from the repository
                var client = await _userRepository.GetClientByIdAsync(clientId);

                if (client == null)
                {
                    return NotFound(new { Message = "Client not found" });
                }

                // Return the IsDemoAccount status
                return Ok(new
                {
                    ClientID = client.ClientID,
                    IsDemoAccount = client.IsDemoAccount
                });
            }
            catch (Exception ex)
            {
                // Log the exception
                return StatusCode(500, new { Message = "An error occurred while retrieving demo account status", Error = ex.Message });
            }
        }

        [HttpPost("updateDemoAccount/{clientId}")]
        public async Task<IActionResult> UpdateDemoAccountStatus(int clientId, [FromBody] UpdateDemoAccountDto model)
        {
            try
            {
                // Get the client from the repository
                var client = await _userRepository.GetClientByIdAsync(clientId);

                if (client == null)
                {
                    return NotFound(new { Message = "Client not found" });
                }

                // Update the IsDemoAccount status
                client.IsDemoAccount = model.IsDemoAccount;

                // Save the changes
                await _userRepository.UpdateClientAsync(client);

                // Return success response
                return Ok(new
                {
                    Success = true,
                    Message = "Demo account status updated successfully",
                    ClientID = client.ClientID,
                    IsDemoAccount = client.IsDemoAccount
                });
            }
            catch (Exception ex)
            {
                // Log the exception
                return StatusCode(500, new { Message = "An error occurred while updating demo account status", Error = ex.Message });
            }
        }




        // 🔹 Get all prompts for a user
        [HttpGet("getprompts/{userId}")]
        public async Task<IActionResult> GetPromptsByUserId(int userId)
        {
            var prompts = await _promptRepository.GetAllPromptsByUserIdAsync(userId);
            if (prompts == null) return NotFound(new { Message = "Prompt not found" });





            return Ok(prompts);
        }

        // Get a specific prompt by ID
        [HttpGet("getprompt/{id}")]
        public async Task<IActionResult> GetPromptById(int id)
        {
            var prompt = await _promptRepository.GetPromptByIdAsync(id);
            if (prompt == null) return NotFound(new { Message = "Prompt not found" });

            return Ok(prompt);
        }

        //Add a new prompt(ID required)


        [HttpPost("addprompt")]
        public async Task<IActionResult> AddPrompt([FromBody] PitchGenApi.Model.Prompt prompt)
        {
            bool test = VerifyPassword("Michael@123", "lkhfhks");
            if (prompt == null || prompt.UserId <= 0)
                return BadRequest(new { Message = "Invalid data" });

            // Ensure CreatedAt is set if not provided
            prompt.CreatedAt ??= DateTime.UtcNow;

            var newPrompt = await _promptRepository.AddPromptAsync(prompt);
            return CreatedAtAction(nameof(GetPromptById), new { id = newPrompt.Id }, newPrompt);
        }

        // 🔹 Update an existing prompt (ID required)
        [HttpPost("updateprompt")]
        public async Task<IActionResult> UpdatePrompt([FromBody] PitchGenApi.Model.Prompt prompt)
        {
            if (prompt == null || prompt.Id <= 0) return BadRequest(new { Message = "Invalid data" });


            var existingPrompt = await _promptRepository.GetPromptByIdAsync(prompt.Id);
            if (existingPrompt == null) return NotFound(new { Message = "Prompt not found" });

            await _promptRepository.UpdatePromptAsync(prompt);
            return NoContent();
        }

        // 🔹 Delete a prompt (ID required)
        [HttpPost("deleteprompt/{id}")]
        public async Task<IActionResult> DeletePrompt(int id)
        {
            if (id <= 0) return BadRequest(new { Message = "Invalid ID" });

            var deleted = await _promptRepository.DeletePromptAsync(id);
            if (!deleted) return NotFound(new { Message = "Prompt not found" });

            return NoContent();
        }




        [HttpPost("process")]
        public async Task<IActionResult> Process([FromBody] ProcessRequest request)
        {
            // Extract parameters from request body
            var searchTerm = request.SearchTerm;
            var instructions = request.Instructions;
            var ModelName = request.ModelName;
            var searchCount = request.SearchCount;
            // Step 1: Call Search
            var searchResult = await Search(searchTerm);

            if (searchResult is OkObjectResult okResult)
            {
                var resultObject = okResult.Value as dynamic;
                var searchResults = resultObject?.Results as List<string>;

                if (searchResults != null && searchResults.Any())
                {
                    string allScrapedData = string.Empty;
                    int maxIterations = Math.Min(searchCount, searchResults.Count); // Use searchCount as maxIterations

                    for (int i = 0; i < maxIterations; i++)
                    {
                        var url = searchResults[i];
                        var scrapeResult = await ScrapeWebsite(url);
                        if (scrapeResult is ObjectResult objectResult && objectResult.StatusCode == 500)
                        {
                            // Return the error response from ScrapeWebsite
                            //return scrapeResult;
                        }
                        else
                        {
                            var scrapedData = ((JsonResult)scrapeResult).Value as dynamic;
                            allScrapedData += scrapedData.ToString().Length > 3000 ? scrapedData.ToString().Substring(0, 2999) : scrapedData.ToString();
                        }
                    }

                    // Step 2: Call GeneratePitch
                    var enquiryRequest = new EnquiryRequest
                    {
                        Prompt = $"{instructions}.{allScrapedData}",
                        ScrappedData = "You are an excellent researcher",
                        ModelName = $"{ModelName}"
                    };

                    var pitchResult = await _pitchservice.GeneratePitchAsync(enquiryRequest);

                    if (!pitchResult.IsSuccess)
                    {
                        return StatusCode(500, new { Message = "Failed to generate pitch", Error = pitchResult.Content });
                    }

                    // Return the pitch response along with allScrapedData and searchResults
                    return Ok(new
                    {
                        PitchResponse = pitchResult,
                        AllScrapedData = allScrapedData,
                        SearchResults = searchResults.Take(maxIterations).ToList() // Only return the results that were processed
                    });
                }
            }

            return BadRequest(new { Message = "Invalid search term or no results found." });
        }






        [HttpPost("search")]
        public async Task<IActionResult> Search([FromBody] string searchTerm)
        {
            var searchResults = await GetSearchResults(searchTerm);
            if (searchResults == null || !searchResults.Any())
            {
                return BadRequest(new { Message = "Invalid search term or no results found." });
            }

            return Ok(new { Results = searchResults });
        }

        private async Task<List<string>> GetSearchResults(string searchTerm)
        {
            string apiKey = "AIzaSyAYB8HNNSiPn3AdPR_KPZqhorBwZq1I_fE";
            string cx = "843f3f01184994648";
            string requestUri = $"https://www.googleapis.com/customsearch/v1?key={apiKey}&cx={cx}&q={Uri.EscapeDataString(searchTerm)}";

            using (HttpClient client = new HttpClient())
            {
                var response = await client.GetAsync(requestUri);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                dynamic searchResult = JsonConvert.DeserializeObject(jsonResponse);

                if (searchResult.items == null)
                {
                    return null;
                }

                var items = (IEnumerable<dynamic>)searchResult.items;
                return items.Select(item => (string)item.link).ToList();
            }
        }

        
        
        
        
        [HttpPost("generatepitch")]
        public async Task<IActionResult> GeneratePitch([FromBody] EnquiryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt) || string.IsNullOrWhiteSpace(request.ModelName))
            {
                return BadRequest(new { Message = "Prompt and ScrappedData are required." });
            }

            var result = await _pitchservice.GeneratePitchAsync(request);

            if (!result.IsSuccess)
            {
                return StatusCode(500, new { Message = "Failed to generate pitch", Error = result.Content });
            }

            return Ok(new { Response = result });

        }


        // No Use SummarizeWithChatGPT
        private async Task<string> SummarizeWithChatGPT(string text)
        {
            string apiKey = "YOUR_OPENAI_API_KEY";
            string apiUrl = "https://api.openai.com/v1/chat/completions";

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var requestBody = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                new { role = "system", content = "Summarize the following text " },
                new { role = "user", content = text }
            },
                    max_tokens = 10000
                };

                var response = await client.PostAsync(apiUrl, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception("Failed to get a response from ChatGPT API.");
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                dynamic result = JsonConvert.DeserializeObject(jsonResponse);

                return result.choices[0].message.content.ToString();
            }
        }

        [HttpPost("sendemail")] // Email Send Code
        public IActionResult SendEmail([FromBody] EmailRequest emailRequest)
        {
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true; //  disables SSL/TLS certificate validation checks.
            if (emailRequest == null || string.IsNullOrWhiteSpace(emailRequest.To) || string.IsNullOrWhiteSpace(emailRequest.Subject) || string.IsNullOrWhiteSpace(emailRequest.Body))
            {
                return BadRequest(new { Message = "Invalid email request data." });
            }

            try
            {
                var fromEmail = "pitchcraft@dataji.co"; // sender email
                var fromPassword = "z7d&73W2f"; //  sender email password

                var smtpClient = new SmtpClient("213.171.222.69") //  SMTP server
                {
                    Port = 587, //  SMTP port
                    Credentials = new NetworkCredential(fromEmail, fromPassword),
                    EnableSsl = true,
                };

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(fromEmail),
                    Subject = emailRequest.Subject,
                    Body = emailRequest.Body,
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(emailRequest.To);

                smtpClient.Send(mailMessage);

                return Ok(new { Message = "Email sent successfully." });
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.Error.WriteLine(ex);

                return StatusCode(500, new { Message = "An error occurred while sending the email." });
            }
        }

        [HttpGet("pitchgenData/{zohoviewId}")]
        public async Task<IActionResult> MyAction(string zohoviewId, string pageToken = null)
        {
            if (string.IsNullOrEmpty(zohoviewId))
            {
                return BadRequest("zohoviewId is required.");
            }

            try
            {
                _logger.LogInformation($"Fetching Zoho data for viewId: {zohoviewId}, pageToken: {pageToken?.Substring(0, Math.Min(10, pageToken?.Length ?? 0))}...");

                // Fetch data for the current page using the pageToken
                var zohoApiResponse = await _zohoService.GetListviewContacts(zohoviewId, pageToken, false);

                // Check if the response itself is null
                if (zohoApiResponse == null)
                {
                    _logger.LogWarning("Zoho API returned null response");
                    return StatusCode(503, new { Message = "Service temporarily unavailable. Please try again." });
                }

                // Check if data is null or empty
                if (zohoApiResponse.Data == null || !zohoApiResponse.Data.Any())
                {
                    _logger.LogInformation("No data found for the provided zohoviewId");
                    return Ok(new
                    {
                        Data = new List<object>(),
                        NextPageToken = (string)null,
                        PreviousPageToken = (string)null,
                        MoreRecords = false,
                        Message = "No data found for the provided zohoviewId."
                    });
                }

                // Return the data along with pagination tokens
                var response = new
                {
                    Data = zohoApiResponse.Data,
                    NextPageToken = zohoApiResponse.Info?.Next_Page_Token,
                    PreviousPageToken = zohoApiResponse.Info?.Previous_Page_Token,
                    MoreRecords = zohoApiResponse.Info?.more_records ?? false
                };

                _logger.LogInformation($"Successfully retrieved {zohoApiResponse.Data.Count} records");
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in pitchgenData endpoint");
                return StatusCode(500, new { Message = "Internal server error", Error = ex.Message });
            }
        }




        [HttpGet("modelsinfo")]
        public async Task<IActionResult> GetModelInfo()
        {
            // Assuming you want to get the first model from a DbSet named ModelRates
            var modelInfo = await _context.ModelRates.ToListAsync();
            return Ok(modelInfo);
        }





        [HttpGet("scrapeWebsite")]
        public async Task<IActionResult> ScrapeWebsite(string url)
        {
            // Get client IP from the request for logging purposes
            string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            // If behind a proxy or load balancer, check X-Forwarded-For header
            if (string.IsNullOrEmpty(clientIp) || clientIp == "::1")
            {
                clientIp = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
            }
            Console.WriteLine($"Client IP: {clientIp}");

            // Avoid hardcoding a specific TLS version to allow negotiation of the best protocol
            // Comment out or remove this line to let the system choose the best protocol
            // ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            try
            {
                // First attempt: Scrape directly without proxy (using your IP)
                Console.WriteLine($"Attempting to scrape directly without proxy for client IP {clientIp}");
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36");
                    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                    client.Timeout = TimeSpan.FromSeconds(60); // Timeout for direct attempt

                    // Parse the URL to check the file extension
                    Uri uri = new Uri(url);
                    if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        // PDF scraping logic
                        byte[] pdfBytes = await client.GetByteArrayAsync(url);

                        using (var pdfStream = new MemoryStream(pdfBytes))
                        using (var pdfDocument = PdfDocument.Open(pdfStream))
                        {
                            string allText = string.Empty;
                            foreach (var page in pdfDocument.GetPages())
                            {
                                allText += page.Text;
                            }

                            // Prepend the URL to the scraped text
                            string result = $"\n\n Data from: {url}\n{allText}";

                            // Return JSON
                            return new JsonResult(new { text1 = result, usedProxy = "No proxy (direct)" });
                        }
                    }
                    else
                    {
                        // HTML scraping logic
                        string htmlContent = await client.GetStringAsync(url);
                        if (string.IsNullOrEmpty(htmlContent))
                        {
                            htmlContent = await client.GetStringAsync(url.Replace("https", "http"));
                        }

                        HtmlDocument htmlDocument = new HtmlDocument();
                        htmlDocument.LoadHtml(htmlContent);

                        // Remove script and style tags
                        foreach (var script in htmlDocument.DocumentNode.Descendants("script").ToArray())
                        {
                            script.Remove();
                        }

                        foreach (var style in htmlDocument.DocumentNode.Descendants("style").ToArray())
                        {
                            style.Remove();
                        }

                        string allText = ExtractAllText(htmlDocument.DocumentNode);

                        // Prepend the URL to the scraped text
                        string result = $"\n\n A Data from: {url}\n{allText}";

                        // Return JSON
                        return new JsonResult(new { text1 = result, usedProxy = "No proxy (direct)" });
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the exception for the direct attempt
                Console.Error.WriteLine($"Direct scrape failed for client IP {clientIp}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine($"Direct Inner Exception: {ex.InnerException.Message}");
                }

                // Fallback: Retry with proxy if the direct attempt fails
                try
                {
                    Console.WriteLine($"Direct scrape failed, retrying with proxy for client IP {clientIp}");
                    // Configure the provided proxy from ToolIP
                    var proxy = new WebProxy
                    {
                        Address = new Uri("http://proxy.toolip.io:31113"), // Updated host and port
                        BypassProxyOnLocal = false,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(
                         "tl-de1e25715025a9478eed694cb5aa497903670049d47d1eb0609c056f20199d35-country-us-session-12efc", // New username
                         "p4k392z1ultm" // New password
                     )
                    };


                    var handler = new HttpClientHandler
                    {
                        Proxy = proxy,
                        UseProxy = true,
                        // Temporarily bypass SSL certificate validation for testing (use with caution)
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                    };

                    using (HttpClient client = new HttpClient(handler))
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36");
                        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                        client.Timeout = TimeSpan.FromSeconds(60); // Timeout for proxy attempt

                        // Parse the URL to check the file extension
                        Uri uri = new Uri(url);
                        if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            // PDF scraping logic
                            byte[] pdfBytes = await client.GetByteArrayAsync(url);

                            using (var pdfStream = new MemoryStream(pdfBytes))
                            using (var pdfDocument = PdfDocument.Open(pdfStream))
                            {
                                string allText = string.Empty;
                                foreach (var page in pdfDocument.GetPages())
                                {
                                    allText += page.Text;
                                }

                                // Prepend the URL to the scraped text
                                string result = $"\n\n Data from: {url}\n{allText}";

                                // Return JSON
                                return new JsonResult(new { text2 = result, usedProxy = "proxy.toolip.io:31113" });
                            }
                        }
                        else
                        {
                            // HTML scraping logic
                            string htmlContent = await client.GetStringAsync(url);
                            if (string.IsNullOrEmpty(htmlContent))
                            {
                                htmlContent = await client.GetStringAsync(url.Replace("https", "http"));
                            }

                            HtmlDocument htmlDocument = new HtmlDocument();
                            htmlDocument.LoadHtml(htmlContent);

                            // Remove script and style tags
                            foreach (var script in htmlDocument.DocumentNode.Descendants("script").ToArray())
                            {
                                script.Remove();
                            }

                            foreach (var style in htmlDocument.DocumentNode.Descendants("style").ToArray())
                            {
                                style.Remove();
                            }

                            string allText = ExtractAllText(htmlDocument.DocumentNode);

                            // Prepend the URL to the scraped text
                            string result = $"\n\n B Data from: {url}\n{allText}";

                            // Return JSON
                            return new JsonResult(new { text2 = result, usedProxy = "proxy.toolip.io:31113" });
                        }
                    }
                }
                catch (Exception proxyEx)
                {
                    Console.Error.WriteLine($"Proxy scrape failed for client IP {clientIp}: {proxyEx.Message}");
                    if (proxyEx.InnerException != null)
                    {
                        Console.Error.WriteLine($"Proxy Inner Exception: {proxyEx.InnerException.Message}");
                    }
                    return StatusCode(500, new { error = "An error occurred during scraping.", details = proxyEx.Message });
                }
            }
        }

        private string ExtractAllText(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                return node.InnerText.Trim();
            }

            if (node.NodeType == HtmlNodeType.Element && node.Name == "script")
            {
                return string.Empty; // Exclude script content
            }

            return string.Join(" ", node.ChildNodes.Select(ExtractAllText).Where(text => !string.IsNullOrWhiteSpace(text)));
        }




        // 🔹 Password Hash Verification
        private bool VerifyPassword(string inputPassword, string storedPasswordHash)
        {
            //using var sha256 = SHA256.Create();
            //var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(inputPassword));
            //var hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            return inputPassword == storedPasswordHash;
        }



        [HttpGet("clientSettings/{clientId}")]
        public async Task<IActionResult> GetClientSettings(int clientId)
        {

            var SettingspgViewIddetails = await _context.Settingspg
                                                  .Where(z => z.ClientId == clientId)
                                                  .ToListAsync();

            if (SettingspgViewIddetails == null || !SettingspgViewIddetails.Any())
            {
                return NotFound(new { Message = "No records found for the given Client ID" });
            }

            return Ok(SettingspgViewIddetails);
        }



        [HttpPost("updateClientSettings/{clientId}")]
        public async Task<IActionResult> UpdateClientSettings(int clientId, [FromBody] ClientSettingsDto updatedSettings)
        {
            try
            {
                var existingSettings = await _context.SettingspgViewIddetails.FirstOrDefaultAsync(z => z.ClientId == clientId);

                if (existingSettings == null)
                {
                    // If settings don't exist, create a new entry
                    existingSettings = new SettingspgViewIddetails
                    {
                        ClientId = clientId,
                        Model_name = updatedSettings.Model_name,
                        Search_URL_count = updatedSettings.Search_URL_count,
                        Search_term = updatedSettings.Search_term,
                        Instruction = updatedSettings.Instruction,
                        System_instruction = updatedSettings.System_instruction,
                        Subject_instruction = updatedSettings.Subject_instruction // Add this line
                                                                                  // ... initialize other properties
                    };

                    _context.SettingspgViewIddetails.Add(existingSettings);
                }
                else
                {
                    // Update existing settings
                    existingSettings.Model_name = updatedSettings.Model_name;
                    existingSettings.Search_URL_count = updatedSettings.Search_URL_count;
                    existingSettings.Search_term = updatedSettings.Search_term;
                    existingSettings.Instruction = updatedSettings.Instruction;
                    existingSettings.System_instruction = updatedSettings.System_instruction;
                    existingSettings.Subject_instruction = updatedSettings.Subject_instruction; // Add this line
                                                                                                
                }

                await _context.SaveChangesAsync();

                return Ok(new { Message = "Settings saved successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving client settings");
                return StatusCode(500, new { Message = "Failed to save settings" });
            }
        }


        [HttpGet("DataFileclientid/{clientId}")]
        public async Task<IActionResult> GetDatafileClientId(int clientId)
        {
            // Check if ClientID exists in the database
            var DataFileDetails = await _context.data_files
                                                  .Where(z => z.client_id == clientId)
                                                  .ToListAsync();

            if (DataFileDetails == null || !DataFileDetails.Any())
            {
                return NotFound(new { Message = "No records found for the given Client ID" });
            }

            return Ok(DataFileDetails);
        }


     




        [HttpPost("updatezoho")]
        public async Task<IActionResult> UpdateZoho([FromBody] UpdateZohoRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { Message = "Invalid request data." });
            }

            try
            {
                bool isUpdated = false;

                if (!string.IsNullOrEmpty(request.ContactId))
                {
                    // Update contact with only the email body
                    isUpdated = await _zohoService.UpdateContacts(request.ContactId, request.EmailBody, request.job_post_URL);
                }
                else if (!string.IsNullOrEmpty(request.AccountId))
                {
                    // Update account with only the email body
                    isUpdated = await _zohoService.UpdateAccount(request.AccountId, request.EmailBody, request.job_post_URL, null, null, false);
                }
                else
                {
                    return BadRequest(new { Message = "Either ContactId or AccountId must be provided." });
                }

                if (isUpdated)
                {
                    return Ok(new { Message = "Zoho CRM email body updated successfully." });
                }
                else
                {
                    return StatusCode(500, new { Message = "Failed to update Zoho CRM email body." });
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.Error.WriteLine(ex);
                return StatusCode(500, new { Message = "An error occurred while updating Zoho CRM email body.", Error = ex.Message });
            }
        }



        // Campaign-related API endpoints

        [HttpGet("campaigns")]
        public async Task<IActionResult> GetAllCampaigns()
        {
            try
            {
                var campaigns = await _context.Campaigns
                    .OrderBy(c => c.CampaignName)
                    .ToListAsync();

                return Ok(campaigns);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving campaigns");
                return StatusCode(500, new { Message = "Failed to retrieve campaigns", Error = ex.Message });
            }
        }

        /// <summary>
        /// Get campaigns by client ID
        /// </summary>
        [HttpGet("campaigns/client/{clientId}")]
        public async Task<IActionResult> GetCampaignsByClientId(int clientId)
        {
            try
            {
                var campaigns = await _context.Campaigns
                    .Where(c => c.ClientId == clientId)
                    .OrderBy(c => c.CampaignName)
                    .ToListAsync();

                if (campaigns == null || !campaigns.Any())
                {
                    return NotFound(new { Message = "No campaigns found for this client" });
                }

                return Ok(campaigns);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving campaigns for client {ClientId}", clientId);
                return StatusCode(500, new { Message = "Failed to retrieve campaigns", Error = ex.Message });
            }
        }

        /// <summary>
        /// Get campaign by ID
        /// </summary>
        [HttpGet("campaigns/{id}")]
        public async Task<IActionResult> GetCampaignById(int id)
        {
            try
            {
                var campaign = await _context.Campaigns.FindAsync(id);

                if (campaign == null)
                {
                    return NotFound(new { Message = "Campaign not found" });
                }

                return Ok(campaign);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving campaign {CampaignId}", id);
                return StatusCode(500, new { Message = "Failed to retrieve campaign", Error = ex.Message });
            }
        }

        /// <summary>
        /// Create a new campaign
        /// </summary>
        [HttpPost("campaigns")]
        public async Task<IActionResult> CreateCampaign([FromBody] CampaignCreateModel model)
        {
            try
            {
                // Validate the model
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                // Verify that PromptId exists
                var promptExists = await _context.Prompts.AnyAsync(p => p.Id == model.PromptId);
                if (!promptExists)
                {
                    return BadRequest(new { Message = "Invalid PromptId. The specified prompt does not exist." });
                }

                // Create new campaign object
                var newCampaign = new Campaign
                {
                    CampaignName = model.CampaignName,
                    PromptId = model.PromptId,
                    ZohoViewId = model.ZohoViewId,
                    ClientId = model.ClientId
                };

                // Add to database
                await _context.Campaigns.AddAsync(newCampaign);
                await _context.SaveChangesAsync();

                // Return the created object
                return CreatedAtAction(nameof(GetCampaignById),
                    new { id = newCampaign.Id },
                    newCampaign);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating campaign");
                return StatusCode(500, new { Message = "An error occurred while creating the campaign", Error = ex.Message });
            }
        }

        /// <summary>
        /// Update an existing campaign
        /// </summary>
        [HttpPost("updatecampaign")]
        public async Task<IActionResult> UpdateCampaign([FromBody] CampaignUpdateModel model)
        {
            try
            {
                // Validate the model
                if (!ModelState.IsValid || model.Id <= 0)
                {
                    return BadRequest(new { Message = "Invalid data or missing campaign ID" });
                }

                // Find the campaign
                var campaign = await _context.Campaigns.FindAsync(model.Id);
                if (campaign == null)
                {
                    return NotFound(new { Message = "Campaign not found" });
                }

                // Verify that PromptId exists
                if (model.PromptId != campaign.PromptId)
                {
                    var promptExists = await _context.Prompts.AnyAsync(p => p.Id == model.PromptId);
                    if (!promptExists)
                    {
                        return BadRequest(new { Message = "Invalid PromptId. The specified prompt does not exist." });
                    }
                }

                // Update campaign properties
                campaign.CampaignName = model.CampaignName;
                campaign.PromptId = model.PromptId;
                campaign.ZohoViewId = model.ZohoViewId;

                // Update in database
                _context.Campaigns.Update(campaign);
                await _context.SaveChangesAsync();

                return Ok(campaign);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating campaign {CampaignId}", model.Id);
                return StatusCode(500, new { Message = "An error occurred while updating the campaign", Error = ex.Message });
            }
        }


        /// <summary>
        /// Delete a campaign
        /// </summary>
        [HttpPost("deletecampaign/{id}")]
        public async Task<IActionResult> DeleteCampaign(int id)
        {
            try
            {
                if (id <= 0)
                {
                    return BadRequest(new { Message = "Invalid ID" });
                }

                // Find the campaign
                var campaign = await _context.Campaigns.FindAsync(id);
                if (campaign == null)
                {
                    return NotFound(new { Message = "Campaign not found" });
                }

                // Remove from database
                _context.Campaigns.Remove(campaign);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Campaign deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting campaign {CampaignId}", id);
                return StatusCode(500, new { Message = "An error occurred while deleting the campaign", Error = ex.Message });
            }
        }






        [HttpPost("update-contact-fields")]
        public async Task<IActionResult> UpdateContactFields([FromBody] ZohoUpdateData request)
        {
            try
            {
                var result = await _zohoService.UpdatePGStatusFields(
                    request.Id,
                    request.Last_Email_Body_Updated,
                    request.PG_Added_Correctly
                );

                if (result)
                {
                    return Ok(new { message = "Zoho Contact updated successfully." });
                }
                else
                {
                    Console.WriteLine($"Zoho update failed for contactId: {request.Id}");
                    return StatusCode(500, new { message = "Failed to update Zoho Contact. Check logs for details." });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while updating contactId {request.Id}: {ex.Message}");
                return StatusCode(500, new { message = "Exception occurred during Zoho update.", error = ex.Message });
            }
        }

        [HttpGet("GetUpdateData")]
        public async Task<IActionResult> GetUpdateData([FromQuery] string zohoViewId, [FromQuery] string pageToken = null)
        {
            if (string.IsNullOrWhiteSpace(zohoViewId))
                return BadRequest("zohoViewId is required.");

            var (filteredData, nextPageToken, previousPageToken, moreRecords) =
                await _zohoService.GetUpdateDataAsync(zohoViewId, pageToken);

            return Ok(new
            {
                Data = filteredData,
                NextPageToken = nextPageToken,
                PreviousPageToken = previousPageToken,
                MoreRecords = moreRecords
            });
        }

    }

}
