---
title: "Sorting"
metaTitle: "Add sorting to your collection fields - EntityGraphQL"
metaDescription: "Add sorting to your collection fields"
---

Being able to sort or order you collections as you query them can be very powerful, especially when paired with paging. To easily add sorting functionality to your collection fields use the `UseSort()` field extension.

```
schema.ReplaceField("people",
    ctx => ctx.People,
    "Return a list of people. Optional sorted")
    .UseSort();
```

If you are using the `SchemaBuilder.FromObject` you can use the `UseSortAttribute` on your collection properties.

```
public class DemoContext : DbContext
{
    [UseSort]
    public DbSet<Movie> Movies { get; set; }
    [UseSort]
    public DbSet<Person> People { get; set; }
    [UseSort]
    public DbSet<Actor> Actors { get; set; }
}
```

This field extension can only be used on a field that has a `Resolve` expression that is assignable to `IEnumerable` - I.e. collections. The extension adds an argument called `sort: <field_name>SortInput`.for example `PeopleSortInput`. The `SortInput` type will have nullable fields for each scalar type in the collection element type. You set which fields you want to use for sorting. Following the above `people` field with the `Person` class defined as:

```
public class Person
{
    public uint Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public DateTime Dob { get; set; }v
    public List<Actor> ActorIn { get; set; }
    public List<Writer> WriterOf { get; set; }
    public List<Movie> DirectorOf { get; set; }
    public DateTime? Died { get; set; }
    public bool IsDeleted { get; set; }
}
```

The GraphQL type will be define like:

```
input PeopleSortInput
{
	id: SortDirectionEnum
	firstName: SortDirectionEnum
	lastName: SortDirectionEnum
	dob: SortDirectionEnum
	died: SortDirectionEnum
	isDeleted: SortDirectionEnum
}

enum SortDirectionEnum {
	ASC
	DESC
}
```

To sort the collection you set the fields with a direction:

```
{
    people(sort: {lastName: DESC}) { lastName }
}

{
    people(sort: {dob: ASC}) { lastName }
}
```

Multiple fields is supported and are taken as ordered

```
{
    people(sort: {dob: ASC, lastName: DESC, firstName: ASC}) { lastName }
}
```

# Default sort

You can set a default sort to be applied if there are no sort arguments passed in the query.

```
schema.ReplaceField("people",
    ctx => ctx.People,
    "Return a list of people. Optional sorted")
    .UseSort((Person person) => person.Dob, SortDirectionEnum.DESC);
```

# Choosing the sort fields

If you use the `UseSort()` method (not the attribute) you can pass in an expression which tells the extension which fields to set in the input type. Make sure you use the correct type for the fields collection.

```
schema.ReplaceField("people",
    ctx => ctx.People,
    "Return a list of people. Optional sorted")
    .UseSort((Person person) => new
    {
        person.Dob,
        person.LastName
    });
```

This will result in only 2 options for sorting.


```
input PeopleSortInput
{
	dob: SortDirectionEnum
	lastName: SortDirectionEnum
}
```