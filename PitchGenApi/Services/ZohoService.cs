using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using PitchGenApi.Model;
using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace PitchGenApi.Services
{
    public class ZohoService
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public ZohoService(IConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration;
            _httpClient = httpClient;
        }

        public async Task<ZohoApiResponse> GetListviewContacts(string cvid, string pageToken, bool showErrorMessage, int per_page = 1)
        {
            try
            {
                string apiUrl = $"https://www.zohoapis.com/crm/v5/Contacts?cvid={cvid}&per_page={per_page}";
                if (!string.IsNullOrEmpty(pageToken))
                {
                    apiUrl += $"&page_token={pageToken}";
                }

                string apiName = "GetCustomViewRecords";
                var (accessKey, timestamp) = await GetApiAccessLog(apiName);

                // First, try with existing token if available
                if (!string.IsNullOrEmpty(accessKey))
                {
                    Console.WriteLine("Trying with existing access token...");
                    var result = await MakeZohoApiRequest(apiUrl, accessKey, showErrorMessage);

                    // If we got data, return it
                    if (result != null && result.Data != null && result.Data.Any())
                    {
                        Console.WriteLine($"Successfully retrieved {result.Data.Count} records with existing token");
                        return result;
                    }

                    Console.WriteLine("No data retrieved with existing token, will refresh...");
                }

                // If we reach here, we need to refresh the token
                Console.WriteLine("Refreshing access token...");
                string refreshTokenCode = _configuration["ZohoCrm:RefreshToken"];
                string newAccessToken = await RefreshToken(refreshTokenCode);

                if (string.IsNullOrEmpty(newAccessToken))
                {
                    Console.WriteLine("Failed to refresh access token");
                    return new ZohoApiResponse();
                }

                // Update the token in database
                string newTimestamp = DateTime.UtcNow.ToString("MM-dd-yyyy HH:mm:ss");
                await UpdateApiAccessLog(apiName, newAccessToken, newTimestamp);
                Console.WriteLine("Token refreshed and saved to database");

                // IMPORTANT: Retry the request with the new token
                Console.WriteLine("Retrying request with new access token...");
                var retryResult = await MakeZohoApiRequest(apiUrl, newAccessToken, showErrorMessage);

                if (retryResult == null)
                {
                    Console.WriteLine("API request returned null even with new token");
                    return new ZohoApiResponse();
                }

                Console.WriteLine($"After retry: Retrieved {retryResult.Data?.Count ?? 0} records");
                return retryResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetListviewContacts: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return new ZohoApiResponse();
            }
        }




        private async Task<ZohoApiResponse> MakeZohoApiRequest(string apiUrl, string accessToken, bool showErrorMessage)
        {
            ZohoApiResponse apiResponse = new ZohoApiResponse();
            int count = 0;
            const int maxRetries = 5;

            while (count <= maxRetries)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

                    Console.WriteLine($"Making API request (attempt {count + 1})...");

                    using var response = await _httpClient.SendAsync(request);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    Console.WriteLine($"Response status: {response.StatusCode}");

                    if (response.IsSuccessStatusCode)
                    {
                        apiResponse = JsonConvert.DeserializeObject<ZohoApiResponse>(responseContent);

                        if (apiResponse != null)
                        {
                            Console.WriteLine($"Successfully retrieved {apiResponse.Data?.Count ?? 0} records");
                            return apiResponse;
                        }

                        return new ZohoApiResponse();
                    }
                    else
                    {
                        if (showErrorMessage || response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            Console.WriteLine($"API Error Response:\nStatus: {response.StatusCode}\nContent: {responseContent}");
                        }

                        // Check if it's an authentication error (expired token)
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            Console.WriteLine("Token is expired (401 Unauthorized)");
                            return null; // Signal that token refresh is needed
                        }

                        // Check if it's a rate limit error
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            count++;
                            if (count <= maxRetries)
                            {
                                Console.WriteLine($"Rate limited, waiting 8 seconds before retry...");
                                await Task.Delay(8000);
                                continue;
                            }
                        }

                        // For other errors, return empty response
                        return new ZohoApiResponse();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception in API request: {ex.Message}");

                    count++;
                    if (count <= maxRetries)
                    {
                        await Task.Delay(8000);
                    }
                }
            }

            return apiResponse;
        }

        private async Task<string> RefreshToken(string refreshToken)
        {
            try
            {
                string clientID = _configuration["ZohoCrm:ClientId"];
                string clientSecret = _configuration["ZohoCrm:ClientSecret"];

                Console.WriteLine($"Attempting to refresh token...");
                Console.WriteLine($"Client ID: {clientID?.Substring(0, Math.Min(10, clientID?.Length ?? 0))}...");
                Console.WriteLine($"Has refresh token: {!string.IsNullOrEmpty(refreshToken)}");

                var url = "https://accounts.zoho.com/oauth/v2/token";
                var parameters = new Dictionary<string, string>
        {
            { "refresh_token", refreshToken },
            { "client_id", clientID },
            { "client_secret", clientSecret },
            { "grant_type", "refresh_token" }
        };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new FormUrlEncodedContent(parameters);

                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"Token refresh response status: {response.StatusCode}");

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    dynamic responseJson = JsonConvert.DeserializeObject(responseContent);
                    string accessToken = responseJson["access_token"];
                    Console.WriteLine($"Successfully refreshed access token: {accessToken?.Substring(0, Math.Min(20, accessToken?.Length ?? 0))}...");
                    return accessToken;
                }
                else
                {
                    Console.WriteLine($"Failed to refresh token. Response: {responseContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing token: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            return string.Empty;
        }


        public async Task<bool> UpdateContacts(string contactId, string newEmailBodyContent, string newJobPostURL)
        {
            string apiName = "GetCustomViewRecords";
            string accessKey = null;
            string timestamp = null;

            (accessKey, timestamp) = await GetApiAccessLog(apiName);

            if (!string.IsNullOrEmpty(timestamp))
                timestamp = timestamp.Replace('/', '-');

            string format = "MM-dd-yyyy HH:mm:ss";
            DateTime parsedTimestamp;

            if (!string.IsNullOrEmpty(accessKey) &&
                DateTime.TryParseExact(timestamp, format,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None,
                                       out parsedTimestamp) &&
                (DateTime.UtcNow - parsedTimestamp).TotalHours < 1)
            {
                return await MakeUpdateContactsRequest(contactId, newEmailBodyContent, newJobPostURL, accessKey);
            }

            string refreshToken = _configuration["ZohoCrm:RefreshToken"];
            string newAccessToken = await RefreshToken(refreshToken);

            string newTimestamp = DateTime.UtcNow.ToString("MM-dd-yyyy HH:mm:ss");
            await UpdateApiAccessLog(apiName, newAccessToken, newTimestamp);

            return await MakeUpdateContactsRequest(contactId, newEmailBodyContent, newJobPostURL, newAccessToken);
        }

        private async Task<bool> MakeUpdateContactsRequest(string contactId, string newEmailBodyContent, string newJobPostURL, string accessKey)
        {
            int retryCount = 0;
            const int maxRetries = 5;

            while (retryCount <= maxRetries)
            {
                try
                {
                    string jsonPayload;

                    if (string.IsNullOrEmpty(newJobPostURL))
                    {
                        jsonPayload = $"{{ \"data\": [ {{ \"Sample_email_body\": \"{newEmailBodyContent.Replace("\"", "\\\"").Replace("\n", "\\n")}\" }} ] }}";
                    }
                    else
                    {
                        jsonPayload = $"{{ \"data\": [ {{ \"Sample_email_body\": \"{newEmailBodyContent.Replace("\"", "\\\"").Replace("\n", "\\n")}\", \"Job_post_URL\": \"{newJobPostURL}\", \"PG_Processed_on1\": \"{DateTime.Now.ToString()}\" }} ] }}";
                    }

                    string apiUrl = $"https://www.zohoapis.com/crm/v5/Contacts/{contactId}";

                    using var request = new HttpRequestMessage(HttpMethod.Put, apiUrl);
                    request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessKey}");
                    request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    else
                    {
                        if (response.StatusCode == HttpStatusCode.TooManyRequests && retryCount < maxRetries)
                        {
                            retryCount++;
                            await Task.Delay(5000);
                            continue;
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating contact: {ex.Message}");
                    retryCount++;
                    if (retryCount <= maxRetries)
                    {
                        await Task.Delay(5000);
                    }
                }
            }

            return false;
        }

        public async Task<bool> UpdateAccount(string accountID, string newEmailBodyContent, string emailSubject, string jobtitle, string emailTag, bool showErrorMessage)
        {
            string apiName = "GetCustomViewRecords";
            string accessKey = null;
            string timestamp = null;

            (accessKey, timestamp) = await GetApiAccessLog(apiName);

            if (!string.IsNullOrEmpty(timestamp))
                timestamp = timestamp.Replace('/', '-');

            string format = "MM-dd-yyyy HH:mm:ss";
            DateTime parsedTimestamp;

            if (!string.IsNullOrEmpty(accessKey) &&
                DateTime.TryParseExact(timestamp, format,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None,
                                       out parsedTimestamp) &&
                (DateTime.UtcNow - parsedTimestamp).TotalHours < 1)
            {
                return await MakeZohoUpdateRequest(accountID, newEmailBodyContent, emailSubject, jobtitle, emailTag, accessKey, showErrorMessage);
            }

            string refreshToken = _configuration["ZohoCrm:RefreshToken"];
            string newAccessToken = await RefreshToken(refreshToken);

            string newTimestamp = DateTime.UtcNow.ToString("MM-dd-yyyy HH:mm:ss");
            await UpdateApiAccessLog(apiName, newAccessToken, newTimestamp);

            return await MakeZohoUpdateRequest(accountID, newEmailBodyContent, emailSubject, jobtitle, emailTag, newAccessToken, showErrorMessage);
        }

        private async Task<bool> MakeZohoUpdateRequest(string accountID, string newEmailBodyContent, string emailSubject, string jobtitle, string emailTag, string accessToken, bool showErrorMessage)
        {
            bool isUpdated = false;

            try
            {
                string escapedEmailBodyContent = newEmailBodyContent.Replace("\"", "\\\"").Replace("\n", "\\n");
                string jsonPayload;

                if (string.IsNullOrEmpty(emailSubject) && string.IsNullOrEmpty(jobtitle) && string.IsNullOrEmpty(emailTag))
                {
                    jsonPayload = $"{{ \"data\": [ {{ \"Email_body\": \"{escapedEmailBodyContent}\" }} ] }}";
                }
                else
                {
                    string escapedEmailSubject = emailSubject?.Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";
                    jsonPayload = $"{{ \"data\": [ {{ \"Email_body\": \"{escapedEmailBodyContent}\", \"Email_subject\": \"{escapedEmailSubject}\", \"PG_Job_title\": \"{jobtitle ?? ""}\", \"Last_used_PG_email_template1\": \"{emailTag ?? ""}\" }} ] }}";
                }

                string apiUrl = $"https://www.zohoapis.com/crm/v5/Accounts/{accountID}";

                using var request = new HttpRequestMessage(HttpMethod.Put, apiUrl);
                request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
                request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    isUpdated = true;
                }
                else
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    if (showErrorMessage)
                    {
                        Console.WriteLine($"Please copy the following response:\n{responseContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (showErrorMessage)
                {
                    Console.WriteLine($"Please copy the following exception details:\n{ex.ToString()}");
                }
            }

            return isUpdated;
        }


        public async Task<AccountInfo> GetAccountInfo(string accountId)
        {
            string apiName = "GetCustomViewRecords";
            string accessKey = null;
            string timestamp = null;

            (accessKey, timestamp) = await GetApiAccessLog(apiName);

            if (!string.IsNullOrEmpty(timestamp))
                timestamp = timestamp.Replace('/', '-');

            string format = "MM-dd-yyyy HH:mm:ss";
            DateTime parsedTimestamp;

            if (!string.IsNullOrEmpty(accessKey) &&
                DateTime.TryParseExact(timestamp, format,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.None,
                                       out parsedTimestamp) &&
                (DateTime.UtcNow - parsedTimestamp).TotalHours < 1)
            {
                return await MakeZohoRequestGetAccountInfo(accountId, accessKey);
            }

            string refreshToken = _configuration["ZohoCrm:RefreshToken"];
            string newAccessToken = await RefreshToken(refreshToken);

            string newTimestamp = DateTime.UtcNow.ToString("MM-dd-yyyy HH:mm:ss");
            await UpdateApiAccessLog(apiName, newAccessToken, newTimestamp);

            return await MakeZohoRequestGetAccountInfo(accountId, newAccessToken);
        }


        private async Task<AccountInfo> MakeZohoRequestGetAccountInfo(string accountId, string accessKey)
        {
            AccountInfo accountInfo = new AccountInfo();
            string apiUrl = $"https://www.zohoapis.com/crm/v5/Accounts/{accountId}?fields=PG_Job_title,Email_body,Email_subject,Last_used_PG_email_template1";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessKey}");

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string jsonContent = await response.Content.ReadAsStringAsync();
                    dynamic jsonResponse = JsonConvert.DeserializeObject(jsonContent);

                    if (jsonResponse.data != null && jsonResponse.data.Count > 0)
                    {
                        dynamic firstItem = jsonResponse.data[0];

                        accountInfo.PG_Job_title = firstItem.PG_Job_title;
                        accountInfo.Email_body = firstItem.Email_body;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting account info: {ex.Message}");
            }

            return accountInfo;
        }


        public async Task<(List<ZohoEmailData> FilteredData, string NextPageToken, string PreviousPageToken, bool? MoreRecords)> GetFilteredZohoDataAsync(string zohoviewId, string pageToken = null)
        {
            if (string.IsNullOrWhiteSpace(zohoviewId))
            {
                throw new ArgumentException("zohoviewId is required.");
            }

            const int perPage = 150;

            try
            {
                var zohoApiResponse = await GetListviewContacts(zohoviewId, pageToken, false, perPage);

                if (zohoApiResponse?.Data == null || !zohoApiResponse.Data.Any())
                {
                    return (new List<ZohoEmailData>(), null, null, false);
                }

                var filteredData = zohoApiResponse.Data.Select(c =>
                {
                    string rawBody = c.Sample_email_body ?? string.Empty;
                    string subject = c.job_post_URL ?? string.Empty;
                    string body = rawBody;

                    if (!string.IsNullOrEmpty(rawBody))
                    {
                        var lines = rawBody.Split('\n');

                        var subjectLine = lines.FirstOrDefault(line => line.Trim().StartsWith("Subject:", StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(subjectLine))
                        {
                            subject = subjectLine.Substring("Subject:".Length).Trim();

                            body = string.Join("\n", lines.Where(line => !line.Trim().StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)));
                        }
                    }

                    return new ZohoEmailData
                    {
                        FullName = c.Full_Name,
                        Email = c.Email,
                        Location = c.Mailing_Country,
                        Company = c.Account_name_friendlySingle_Line_12,
                        website = c.Website,
                        linkedin_URL = c.LinkedIn_URL,
                        JobTitle = c.Job_Title,
                        Subject = subject,
                        Body = body
                    };
                }).ToList();

                return (
                    filteredData,
                    zohoApiResponse.Info?.Next_Page_Token,
                    zohoApiResponse.Info?.Previous_Page_Token,
                    zohoApiResponse.Info?.more_records
                );
            }
            catch (Exception ex)
            {
                throw new Exception("Error occurred while fetching Zoho data.", ex);
            }
        }

        public async Task<bool> UpdatePGStatusFields(string contactId, DateTime? lastEmailUpdateTimestamp, bool? pgAddedCorrectly)
        {
            if (string.IsNullOrWhiteSpace(contactId))
                return false;

            string requestUrl = $"https://www.zohoapis.com/crm/v2/Contacts/{contactId}";
            string accessToken = _configuration["ZohoCrm:AccessToken"];
            string refreshToken = _configuration["ZohoCrm:RefreshToken"];

            string jsonPayload = $@"
    {{
      ""data"":[
                {{
                    ""Last_Email_Body_updated"": ""{lastEmailUpdateTimestamp:yyyy-MM-ddTHH:mm:ssZ}"",
                    ""PG_added_correctly"": {pgAddedCorrectly.ToString().ToLower()}
                }}
                ]
    }}";

            using var request = new HttpRequestMessage(HttpMethod.Put, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", accessToken);
            request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Zoho Response (1st attempt): " + responseBody);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Console.WriteLine("Access token expired. Refreshing...");

                string newAccessToken = await RefreshToken(refreshToken);
                if (string.IsNullOrEmpty(newAccessToken))
                {
                    Console.WriteLine("Token refresh failed.");
                    return false;
                }

                using var retryRequest = new HttpRequestMessage(HttpMethod.Put, requestUrl);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", newAccessToken);
                retryRequest.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                response = await _httpClient.SendAsync(retryRequest);
                responseBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine("Zoho Response (after refresh): " + responseBody);
            }

            return response.IsSuccessStatusCode;
        }


        public async Task<(List<ZohoUpdateData> FilteredData, string NextPageToken, string PreviousPageToken, bool? MoreRecords)> GetUpdateDataAsync(string zohoviewId, string pageToken = null)
        {
            if (string.IsNullOrWhiteSpace(zohoviewId))
                throw new ArgumentException("zohoviewId is required.");

            const int perPage = 1;

            try
            {
                var zohoApiResponse = await GetListviewContacts(zohoviewId, pageToken, false, perPage);

                if (zohoApiResponse?.Data == null || !zohoApiResponse.Data.Any())
                {
                    return (new List<ZohoUpdateData>(), null, null, false);
                }

                var filteredData = zohoApiResponse.Data.Select(c => new ZohoUpdateData
                {
                    Id = c.id,
                    Last_Email_Body_Updated = c.Last_Email_Body_updated,                 
                    PG_Added_Correctly = c.PG_added_correctly                           
                }).ToList();

                return (
                    filteredData,
                    zohoApiResponse.Info?.Next_Page_Token,
                    zohoApiResponse.Info?.Previous_Page_Token,
                    zohoApiResponse.Info?.more_records
                );
            }
            catch (Exception ex)
            {
                throw new Exception("Error occurred while fetching Zoho data.", ex);
            }
        }
        private async Task<(string, string)> GetApiAccessLog(string apiName)
        {
            var connectionString = _configuration.GetConnectionString("UATConnection");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                SqlCommand cmd = new SqlCommand("GetApiAccessLog", conn);
                cmd.CommandType = CommandType.StoredProcedure;

                cmd.Parameters.AddWithValue("@api_name", apiName);

                await conn.OpenAsync();
                using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        string accessKey = reader["access_key"].ToString();
                        string dateTimeStamp = reader["datetime_stamp"].ToString();
                        return (accessKey, dateTimeStamp);
                    }
                }
            }

            return (null, null);
        }
        private async Task UpdateApiAccessLog(string apiName, string accessKey, string dateTimeStamp)
        {
            var connectionString = _configuration.GetConnectionString("UATConnection");

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                SqlCommand cmd = new SqlCommand("UpdateApiAccessLog", conn);
                cmd.CommandType = CommandType.StoredProcedure;

                cmd.Parameters.AddWithValue("@api_name", apiName);
                cmd.Parameters.AddWithValue("@access_key", accessKey);
                cmd.Parameters.AddWithValue("@datetime_stamp", dateTimeStamp);

                await conn.OpenAsync();
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}