using System;
using PharmaGo.Domain.Entities;

namespace PharmaGo.UsersService.Models.In
{
	public class StockRequestDetailModelRequest
    {
        public DrugModelRequest Drug { get; set; }
        public int Quantity { get; set; }
	}
}

