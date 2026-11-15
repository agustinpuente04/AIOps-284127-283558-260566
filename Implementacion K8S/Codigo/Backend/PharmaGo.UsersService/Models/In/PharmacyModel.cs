using PharmaGo.Domain.Entities;

namespace PharmaGo.UsersService.Models.In
{
    public class PharmacyModel
    {
        public string Name { get; set; }
        public string Address { get; set; }

        public Pharmacy ToEntity()
        {
            return new Pharmacy()
            {
                Name = Name,
                Address = Address,
                Users = new List<User>(),
                Drugs = new List<Drug>()
            };
        }
    }
}
