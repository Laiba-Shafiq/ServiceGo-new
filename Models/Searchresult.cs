using System.Collections.Generic;

namespace ServiceGo.Models
{
    public class Searchresult
    {
        public string SelectedService { get; set; } = string.Empty;
        public List<ProviderDetail> Providers { get; set; } = new List<ProviderDetail>();
    }

    public class ProviderDetail
    {
        public int ProviderId { get; set; }
        public string? Name { get; set; }
        public string? ServiceCategory { get; set; }
        public string? City { get; set; }
        public string? ShopAddress { get; set; }
        public decimal Rating { get; set; }
        public int ReviewsCount { get; set; }
    }
}
