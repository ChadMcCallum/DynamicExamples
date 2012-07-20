using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Dynamic;
using System.Linq;

namespace DynamicConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            //expando object, comes with .NET 4.0
            //notice the "dynamic" keyword - basically tells the compiler to avoid any property or method validation
            dynamic expando = new ExpandoObject();
            expando.Name = "Chad";
            expando.Address = new ExpandoObject();
            expando.Address.Street = "967 Dorothy St";
            expando.Address.City = "Regina";

            //notice the code completion here
            Console.WriteLine(expando.Address.Street);
            Console.WriteLine(expando.Address.City);
            Console.WriteLine(expando.Name);

            //our own expando object
            dynamic myexpando = new OurOwnExpando();
            myexpando.Name = "Chad";
            myexpando.Address = "967 Dorothy St";
            myexpando.Address.City = "Regina";

            //by default, Console.WriteLine will ask for an "object", the ToString will just return 
            //"[DynamicConsole.OurOwnExpando]" - casting it specifically will trigger a call to TryConvert
            Console.WriteLine((string)myexpando.Address);
            Console.WriteLine((string)myexpando.Address.City);
            Console.WriteLine((string)myexpando.Name);

            dynamic context = new DynamicDatabase(new SqlConnection(
                "Data Source=localhost;Initial Catalog=Northwind;Integrated Security=True"));
            dynamic result = context.Customers.SelectAll();
            Console.WriteLine("Found " + result.Count + " rows");
            foreach(var row in result)
            {
                Console.WriteLine();
            }
            Console.WriteLine("First company name: " + result[0].CompanyName);
            Console.ReadLine();
            
            dynamic filteredResult = context.Customers.SelectByID(CustomerID: "EASTC");
            Console.WriteLine("Found " + filteredResult.Count + " rows");
            Console.WriteLine("Company name: " + filteredResult[0].CompanyName);
            Console.ReadLine();
        }
    }

    class OurOwnExpando : DynamicObject
    {
        //expando objects are basically dictionaries of property names and values
        private Dictionary<string, dynamic> propertybag;
        //our own internal value, think of it like a linked list
        private object _value;

        public OurOwnExpando()
        {
            propertybag = new Dictionary<string, dynamic>();
        }

        public OurOwnExpando(object value) : this()
        {
            _value = value;
        }

        //whenever we try to set any property, this method is called
        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            //binder.Name represents the property we're trying to set
            //value is what we're trying to set the property to
            propertybag[binder.Name] = new OurOwnExpando(value);

            //return true to indicate the call worked successfully
            //if we return false, it will throw a runtime exception
            return true;
        }

        //whenever we try to get a property, this method is called
        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            //binder.Name represents the property we're trying to get
            if (propertybag.ContainsKey(binder.Name))
            {
                //get the value from our dictionary
                result = propertybag[binder.Name];
                return true;
            }

            //we haven't set this property yet, throw a runtime exception
            result = null;
            return false;
        }

        //whenever we try to cast the dynamic object to a specific type, this method is called
        public override bool TryConvert(ConvertBinder binder, out object result)
        {
            //binder.ReturnType represents the type we're trying to cast to
            result = Convert.ChangeType(_value, binder.ReturnType);
            return true;
        }
    }

    //this class represents a connection to a database and the tables it contains
    class DynamicDatabase : DynamicObject
    {
        private readonly SqlConnection _connection;

        public DynamicDatabase(SqlConnection connection)
        {
            _connection = connection;
        }

        //whenever we try to get a member, verify a table of the same name exists in the DB
        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            var cmd = new SqlCommand("EXEC sp_tables", _connection);
            cmd.Parameters.AddWithValue("@table_name", binder.Name);
            cmd.Connection = _connection;
            _connection.Open();
            var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
            if (!reader.HasRows)
            {
                //if the table doesn't exist, throw a runtime exception
                result = null;
                return false;
            }
            _connection.Close();
            //return a DynamicTable object to wrap the table
            result = new DynamicTable(binder.Name, _connection);
            return true;
        }
    }

    //represents a table in a database we can call stored procedures against
    class DynamicTable : DynamicObject
    {
        private readonly string _table;
        private readonly SqlConnection _connection;

        public DynamicTable(string table, SqlConnection connection)
        {
            _table = table;
            _connection = connection;
        }

        //whenever we try to execute a method on a dynamic object, this method is called
        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            //our stored procedures in the DB take the format "TableName_Operation"
            //binder.Name represents the name of the method we're trying to call
            var cmd = new SqlCommand(string.Format("{0}_{1}", _table, binder.Name), _connection) 
                { CommandType = CommandType.StoredProcedure };

            //if the caller passed arguments into the dynamic method, figure out the names of those arguments
            //note this only works with named parameters, i.e. SomeMethod(argName: value, argName2: value)
            //for (var i = 0; i < args.Length; i++)
            //{
            //    var name = binder.CallInfo.ArgumentNames[i];
            //    cmd.Parameters.AddWithValue(name, args[i]);
            //}

            
            //instead of using method argument names, ask SQL Server what the parameter names should be
            _connection.Open();
            SqlCommandBuilder.DeriveParameters(cmd);
            _connection.Close();
            for (int i = 0; i < args.Length; i++)
            {
                //first param is RETURN_VALUE, skip it
                var param = cmd.Parameters[i + 1];
                param.Value = args[i];
            }


            var adapter = new SqlDataAdapter(cmd);
            var table = new DataTable();
            try
            {
                _connection.Open();
                adapter.Fill(table);
                //create a dynamic object to wrap the result data table
                result = new DynamicDataTable(table);
            }
            catch
            {
                result = null;
            }
            finally
            {
                _connection.Close();
            }
            return (result != null);
        }
    }

    //this object represents a filled in DataTable object and its rows
    class DynamicDataTable : DynamicObject
    {
        private readonly DataTable _table;

        public DynamicDataTable(DataTable table)
        {
            _table = table;
        }

        //whenever we try to access an indexer on a dynamic object, this method is called (i.e. object[3])
        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            //if more than one indexer (i.e. [1, 2]), fail
            if (indexes.Length < 1 || indexes.Length > 1)
            {
                result = null;
                return false;
            }
            if (indexes[0] is int)
            {
                var index = (int)indexes[0];
                if (index >= _table.Rows.Count || index < 0)
                {
                    result = null;
                    return false;
                }
                //return a dynamic data row object that wraps a DataRow
                result = new DynamicDataRow(_table.Rows[(int)indexes[0]]);
                return true;
            }
            result = null;
            return false;
        }

        //our own defined property - note that the dynamic runtime looks for these first before defaulting to TryGetMember
        public int Count { get { return _table.Rows.Count; } }
    }

    //represents a data row
    class DynamicDataRow : DynamicObject
    {
        private readonly DataRow _row;

        public DynamicDataRow(DataRow row)
        {
            _row = row;
        }

        //when we try to access a property, pull data from the respective column
        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            //if our internal data row object contains a column of the same name
            //as the property requested, return that column's data.
            if (_row.Table.Columns.Contains(binder.Name))
            {
                result = _row[binder.Name];
                return true;
            }

            result = null;
            return false;
        }
    }
}
