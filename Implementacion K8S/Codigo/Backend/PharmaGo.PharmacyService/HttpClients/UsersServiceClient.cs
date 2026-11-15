using PharmaGo.Domain.Entities;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PharmaGo.PharmacyService.HttpClients
{
    public class UsersServiceClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<UsersServiceClient> _logger;

        public UsersServiceClient(HttpClient httpClient, ILogger<UsersServiceClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<User?> GetUserByIdAsync(int id)
        {
            try
            {
                var response = await _httpClient.GetAsync($"/api/users/{id}");
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<User>(content, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    });
                }
                
                _logger.LogWarning($"Failed to get user {id} from UsersService. Status: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error calling UsersService to get user {id}");
                return null;
            }
        }
    }
}
