using Sakila.DomainModel;
using ServiceStack.DesignPatterns.Translator;

namespace ServiceStack.Sakila.Logic.Translators.DomainToData
{
	public class CustomerToDataTranslator : ITranslator<DataAccess.DataModel.Customer, Customer>
	{
		public static readonly CustomerToDataTranslator Instance = new CustomerToDataTranslator();

		public DataAccess.DataModel.Customer Parse(Customer from)
		{
			var to = new DataAccess.DataModel.Customer {
				Id = (ushort)from.Id,
			};

			return to;
		}
	}
}