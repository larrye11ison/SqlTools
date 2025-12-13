# SqlTools

An application written in .net 8 and WPF for searching and browsing object scripts on Microsoft SQL Servers.

SqlTools can search by any or all of the following:

* Object Name
* Schema Name
* Object Definition - you can search *inside* the text of the objects' definitions
* After a search is complete, you can further filter within the search results
 * Simply type one or more "search tokens" - words or parts of words separated by text
 * You can also filter specific fields within the results grid, ex.:
  * type:stor  | filters the Type column to values containing "stor"
  * db:foo  | filters the Database column to values containing "foo"
  * there are at least 1 or 2 others that I cannot remember right now

## Can you show me what it looks like?

Yes.

This is the interface for locating objects by searching on the SQL Server:

![Code View Interface](/Wiki/Images/SqlToolsSearchInterface.png)

Clicking the "Script" button obtains the SQL object definition for that object and then displays it to you in this interface:

![Code View Interface](/Wiki/Images/SqlToolsCodeViewInterface.png)

## Why do we need yet another way to view and script objects on MSSQL?

In my daily job I do a lot of prod support work on servers containing dozens of databases, 
many of which have several thousand objects. I constantly need to find stored procs, views, 
etc. by name, but most of the time I don't remember the FULL name of the object I'm looking 
for. To make matters worse, a lot of times I don't even remember which *database* it's in.

The search interface(s) in SQL Management Studio were always very cumbersome, so most of the time I was running queries
like this over and over:

```
select *
from foo.sys.tables t
where t.name like '%something%'
```

...so I decided to make an application that would do this for me, with the added benefit of searching 
across all databases on a server (if desired) as well as also searching within the objects' definitions as well.

I have come across tools that do similar things, but they had one or more of the following problems:

* They were very buggy
* They tried to do too much and didn't have enough focus on just *finding stuff*
* They were super-expensive

## Can you tell me other interesting facts about this thing?

Sure, I've got nothing better to do...

* Supports auth via Windows or SQL login with username/password
* The app's icon is a totally awesome "recliner" because it makes your life so much easier, you'll have more time to relax in your easy chair
* When searching for objects on the server:
  * Allows you to search by object name, schema name and/or within object definition (including tables!)
  * Can search across individual DB's on a server or all of them
  * Searching across all DB's on a server doesn't generally impact the system too much
  * Each query against an individual database is run asynchronously, so searching on a server with a large number of DB's will still happen quickly
  * After you search, you can use somewhat sophisticated filtering techniques to narrow down those results on the client-side
* When viewing object definition scripts
  * Syntax highlighting, mainly thanks to [Avalon Edit](http://avalonedit.net/)
  * Has option to canonicalize and reformat the object code using `Microsoft.SqlServer.TransactSql.ScriptDom`.
  * Incremental find within object definition scripts.

## Requirements

* .net 4.8 or higher 
* Windows 11, but probably Win 10.
  * I doubt it would run on Linux - never needed to try.
* An MS SQL 2008 or higher database... _probably_. 
  * This tool was developed years ago and was initially used extensively on SQL 2008. 
  * Over the years, my work environment 
    upgraded to 2014, then 2019, and the tool has always worked fine the whole time.
  * But I haven't tried to hit anything older than 2014 for almost 5 years, and it's been at 
    least a full year since I've tried anything older than 2019. I also haven't yet tried hitting
    anything newer than 2019.
  * As of Dec 2025, the current codebase has been upgraded - all the latest nuget packages for 
    data access and "ScriptDom" have been updated. So there's now even a bit chance that older 
    MSSQL platforms may have issues, so beware.
    
