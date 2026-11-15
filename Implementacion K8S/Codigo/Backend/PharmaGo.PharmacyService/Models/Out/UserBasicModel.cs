using PharmaGo.Domain.Entities;

namespace PharmaGo.PharmacyService.Models.Out
{
    public class UserBasicModel
    {
        public int Id { get; set; }
        public string UserName { get; set; }

        public UserBasicModel(User user)
        {
            Id = user.Id;
            UserName = user.UserName;
        }
    }
}
