# NuGet Catalog Reader

This project presents a distributed and eventually consistent [NuGet catalog](https://docs.microsoft.com/en-us/nuget/api/catalog-resource) reader. This can be used to index all packages on nuget.org.

## Problem

* Explain catalog resource: "A well-ordered queue of package events on a package source"
* Explain why catalog must be processed in order for individual packages
    * Package listing hides package from search results
    * Example 1
        * Two catalog leafs: unlist package A, list package A
        * End result is that package A should be listed
        * If leafs are processed out-of-order, package A will be unlisted
    * Example 2
        * Two catalog leafs: unlist package A, unlist package B
        * End result is that both package A and package B shoudl be unlisted
        * Order of processing leafs does not matter
* Explain catalog cursors

## Poor Solution #1

Create a queue from catalog leaves and have many jobs processing leaves. Use distributed locks for consistency.

Problem: Say 100 versions of package A are unlisted. Only one job can acquire the lock at a time, all other jobs will be blocked.

Learning: A package should be processed by only one job at a time.

## Poor Solution #2

Partition catalog leaves on package IDs using something like consistent hashing, and process each partition by a single job.

Problem: Difficult to increase/decrease partitions

Problem: You need one catalog cursor per partition, and one overall cursor

Problem: Poor load balancing. A single package that is frequently updated may cause load for a single partition and its other packages.

Learning: Partitioning is simple to implement.

Solution: What if for each package ID we had a separate partition, queue, and job? Sounds like an infrastructure nightmare... unless we use the actor model!

## Solution #3

We can use the actor model:

1. Actors are asynchronous. We can queue catalog leafs.
1. Actors are single-threaded and process messages in order. We can create an actor for each package id.
1. Actors are stateful. We can track pending leafs and maintain cursors.

**TODO**: Insert diagram here...

### "Catalog Leaf Processor" actor

The first actor is the "Catalog Leaf Processor"; it processes catalog leaves. An instance of this actor is created for each package ID. Once a leaf is processed, this actor notifies the "Catalog Processor". This actor is stateless.

### "Catalog Processor" actor

The second actor is the "Catalog Processor"; it enqueues work to the "Catalog Leaf Processor" actors and maintains catalog cursors.

The "Catalog Processor" maintains state:

1. A list of "pending" catalog leafs that are undergoing processing
2. A "public" cursor of processed catalog leafs
3. An "internal" cursor of catalog leafs that are processing

The "Catalog Processor" is called periodically to find and then enqueue new catalog leafs for processing. The enqueued leafs' greatest catalog commit timestamp becomes the new "internal" cursor.

The "Catalog Processor" is also called whenever a "Catalog Leaf Processor" actor finishes processing a catalog leaf. This will remove the finished leaf from the "pending" leafs and will update the "public" cursor if necessary.
