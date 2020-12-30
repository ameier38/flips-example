#r "nuget: Flips"
#r "nuget: FSharp.UMX"
#r "nuget: Deedle"

open Deedle
open Flips
open Flips.Types
open Flips.SliceMap
open Flips.UnitsOfMeasure
open FSharp.UMX

let rocksPath = __SOURCE_DIRECTORY__ + "/data/rocks.csv"
let solutionPath = __SOURCE_DIRECTORY__ + "/data/solution.csv"

type [<Measure>] usd and USD = float<usd>
type [<Measure>] kg and Kg = float<kg>
type [<Measure>] bucketId and BucketId = string<bucketId>
type [<Measure>] rockId and RockId = string<rockId>

type RockType =
    | Gold
    | Silver
    | Bronze
    static member Parse(s:string) =
        match s with
        | "Gold" -> Gold
        | "Silver" -> Silver
        | "Bronze" -> Bronze
        | other -> failwith $"invalid rock type {other}"

type Bucket =
    { BucketId: BucketId
      Capacity: Kg
      Gold: Kg
      Silver: Kg
      Bronze: Kg
      Discount: float }

type Rock =
    { RockId: RockId
      RockType: RockType
      Weight: Kg }

let getRockPrice (rock:Rock) (bucket:Bucket) =
    let pricePerKg =
        match rock.RockType with
        | Gold -> 0.98<usd/kg>
        | Silver -> 0.95<usd/kg>
        | Bronze -> 0.92<usd/kg>
    pricePerKg * (1.0 - bucket.Discount) * rock.Weight

// Inputs

let maxGoldConcentration = 0.8
let maxSilverConcentration = 0.5
let maxBronzeConcentration = 0.3

let buckets =
    [ { BucketId = %"B1"; Capacity = 40.0<kg>; Gold = 5.0<kg>; Silver = 10.0<kg>; Bronze = 5.0<kg>;  Discount = 0.1; }
      { BucketId = %"B2"; Capacity = 50.0<kg>; Gold = 10.0<kg>; Silver = 5.0<kg>; Bronze = 5.0<kg>; Discount = 0.08 }
      { BucketId = %"B3"; Capacity = 30.0<kg>; Gold = 5.0<kg>; Silver = 0.0<kg>; Bronze = 2.0<kg>; Discount = 0.0 }
      { BucketId = %"B4"; Capacity = 10.0<kg>; Gold = 1.0<kg>; Silver = 1.0<kg>; Bronze = 0.5<kg>; Discount = 0.05 } ]

let bucketCapacity: SMap<BucketId, Kg> =
    [ for bucket in buckets do
        bucket.BucketId, bucket.Capacity ]
    |> SMap

let totalBucketCapacity =
    buckets
    |> List.sumBy (fun b -> b.Capacity)

printfn $"Total bucket capacity: {totalBucketCapacity}"

let rocksFrame = Frame.ReadCsv(rocksPath)

let rocks =
    rocksFrame.Rows
    |> Series.mapValues(fun s ->
        { RockId = s.GetAs<RockId>("RockId")
          RockType = s.GetAs<string>("RockType") |> RockType.Parse
          Weight = s.GetAs<Kg>("Weight") })
    |> Series.values
    |> Seq.toList

let totalRockWeight =
    rocks
    |> List.sumBy (fun r -> r.Weight)

printfn $"Total rock weight: {totalRockWeight}"

let rockPrice: SMap2<RockId, BucketId, USD> =
    [ for rock in rocks do
        for bucket in buckets do
            (rock.RockId, bucket.BucketId),
            getRockPrice rock bucket ]
    |> SMap2

let rockWeight: SMap2<RockId, RockType, Kg> =
    [ for rock in rocks do
        (rock.RockId, rock.RockType), rock.Weight]
    |> SMap2

// Decisions

let rockAddedToBucket: SMap2<RockId, BucketId, Decision<1>> =
    [ for rock in rocks do
          for bucket in buckets do
            let rockId, bucketId = rock.RockId, bucket.BucketId
            (rockId, bucketId),
            Decision.createBoolean $"{rockId}_added_to_{bucketId}" ]
    |> SMap2.ofList

// Objective

let totalPrice = sum (rockAddedToBucket .* rockPrice)

let ``Maximize total price`` = Objective.create "MaximizeTotalPrice" Maximize totalPrice

// Constraints

let ``Rock can only be added to one bucket`` =
    [ for rock in rocks do 
        Constraint.create $"{rock.RockId}_can_only_be_added_to_one_bucket"
            (sum rockAddedToBucket.[rock.RockId, All] <== 1.0 ) ]

let createConcentrationConstraint (rockType:RockType) (concentrationLimit:float) =
    [ for bucket in buckets do
        let currentRockWeight =
            match rockType with
            | Gold -> bucket.Gold
            | Silver -> bucket.Silver
            | Bronze -> bucket.Bronze
        let currentWeight = bucket.Gold + bucket.Silver + bucket.Bronze
        let rockWeightAdded = sum (rockAddedToBucket.[All, bucket.BucketId] .* rockWeight.[All, Equals rockType])
        let weightAdded = sum (rockAddedToBucket.[All, bucket.BucketId] .* rockWeight)
        let newRockWeight = currentRockWeight + rockWeightAdded
        let newWeight = currentWeight + weightAdded
        let rockWeightLimit = concentrationLimit * newWeight
        Constraint.create $"{bucket.BucketId}_{rockType}_concentration_below_limit" (newRockWeight <== rockWeightLimit) ]

let ``Bucket weight must be less than or equal to capacity`` =
    [ for bucket in buckets do
        let currentWeight = bucket.Gold + bucket.Silver + bucket.Bronze
        let weightAdded = sum (rockAddedToBucket.[All, bucket.BucketId] .* rockWeight)
        let totalWeight = currentWeight + weightAdded
        let totalCapacity = bucketCapacity.[bucket.BucketId]
        Constraint.create $"{bucket.BucketId}_total_weight_must_be_less_than_capacity" (totalWeight <== totalCapacity) ]

// Model

let model =
    Model.create ``Maximize total price``
    |> Model.addConstraints ``Rock can only be added to one bucket``
    |> Model.addConstraints (createConcentrationConstraint Gold maxGoldConcentration)
    |> Model.addConstraints (createConcentrationConstraint Silver maxSilverConcentration)
    |> Model.addConstraints (createConcentrationConstraint Bronze maxBronzeConcentration)
    |> Model.addConstraints ``Bucket weight must be less than or equal to capacity``

// Solve

#time "on"
let result = Solver.solve { Settings.basic with MaxDuration = 30_000L } model
#time "off"

match result with
| Optimal solution ->
    printfn "Total price: $%f" (Objective.evaluate solution ``Maximize total price``)

    let solutionFrame =
        Solution.getValues solution rockAddedToBucket
        |> Map.toList
        |> List.groupBy (fun ((rockId, _), _) -> rockId)
        |> List.map (fun (rockId, records) ->
            let values =
                records
                |> List.map (fun ((_, bucketId), value) ->
                    bucketId => if value > 0.0 then "x" else "" )
            rockId => series values)
        |> Frame.ofRows

    solutionFrame.SaveCsv(solutionPath)
| other -> printfn "Failed to solve: %A" other
