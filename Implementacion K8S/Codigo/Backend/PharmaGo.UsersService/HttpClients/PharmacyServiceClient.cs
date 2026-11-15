using PharmaGo.Domain.Entities;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PharmaGo.UsersService.HttpClients
{
    public class PharmacyServiceClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PharmacyServiceClient> _logger;

        public PharmacyServiceClient(HttpClient httpClient, ILogger<PharmacyServiceClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<Pharmacy?> GetPharmacyByIdAsync(int id)
        {
            try
            {
                var response = await _httpClient.GetAsync($"/api/pharmacy/{id}");
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<Pharmacy>(content, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    });
                }
                
                _logger.LogWarning($"Failed to get pharmacy {id} from PharmacyService. Status: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error calling PharmacyService to get pharmacy {id}");
                return null;
            }
        }
    }
}
