#r "nuget: Flips"
#r "nuget: FSharp.UMX"

open Flips
open Flips.Types
open Flips.SliceMap
open Flips.UnitsOfMeasure
open Flips.UnitsOfMeasure.Types
open FSharp.UMX


type [<Measure>] usd and USD = float<usd>
type [<Measure>] kg and Kg = float<kg>
type [<Measure>] rating and Rating = float<rating>
type [<Measure>] bucketId and BucketId = string<bucketId>
type [<Measure>] rockId and RockId = string<rockId>

type RockType =
    | Gold
    | Silver
    | Bronze

type Bucket =
    { BucketId: BucketId
      Capacity: Kg
      Rating: Rating }

type Rock =
    { RockId: RockId
      RockType: RockType
      Weight: Kg }

let getRockPrice (rock:Rock) (bucketRating:Rating) =
    let pricePerKg =
        match rock.RockType, bucketRating with
        | Gold, r when r < 1.0<rating> -> 0.9<usd/kg>
        | Gold, r when r < 1.1<rating> -> 0.95<usd/kg>
        | Gold, _ -> 1.0<usd/kg>
        | Silver, r when r < 1.0<rating> -> 0.88<usd/kg>
        | Silver, r when r < 1.1<rating> -> 0.93<usd/kg>
        | Silver, _ -> 0.95<usd/kg>
        | Bronze, r when r < 1.0<rating> -> 0.85<usd/kg>
        | Bronze, r when r < 1.1<rating> -> 0.98<usd/kg>
        | Bronze, _ -> 0.93<usd/kg>
    pricePerKg * rock.Weight

// Inputs

let buckets =
    [ { BucketId = %"B1"; Capacity = 100.0<kg>; Rating = 1.03<rating> }
      { BucketId = %"B2"; Capacity = 250.0<kg>; Rating = 0.98<rating> }
      { BucketId = %"B3"; Capacity = 20.0<kg>; Rating = 1.01<rating> }
      { BucketId = %"B4"; Capacity = 80.0<kg>; Rating = 1.13<rating> } ]

let rocks =
    [ { RockId = %"R1"; RockType = Gold; Weight = 1.2<kg> }
      { RockId = %"R2"; RockType = Silver; Weight = 0.2<kg> }
      { RockId = %"R3"; RockType = Bronze; Weight = 0.5<kg> }
      { RockId = %"R4"; RockType = Gold; Weight = 2.0<kg> }
      { RockId = %"R5"; RockType = Gold; Weight = 4.0<kg> }
      { RockId = %"R6"; RockType = Silver; Weight = 3.2<kg> }
      { RockId = %"R7"; RockType = Bronze; Weight = 5.3<kg> }
      { RockId = %"R8"; RockType = Gold; Weight = 1.0<kg> }
      { RockId = %"R9"; RockType = Silver; Weight = 0.8<kg> }
      { RockId = %"R10"; RockType = Bronze; Weight = 4.6<kg> }
      { RockId = %"R11"; RockType = Gold; Weight = 7.8<kg> }
      { RockId = %"R12"; RockType = Silver; Weight = 10.1<kg> }
      { RockId = %"R13"; RockType = Bronze; Weight = 1.5<kg> }
      { RockId = %"R14"; RockType = Gold; Weight = 4.2<kg> }
      { RockId = %"R15"; RockType = Silver; Weight = 3.7<kg> }
      { RockId = %"R16"; RockType = Bronze; Weight = 10.9<kg> }
      { RockId = %"R17"; RockType = Gold; Weight = 20.2<kg> }
      { RockId = %"R18"; RockType = Silver; Weight = 1.9<kg> }
      { RockId = %"R19"; RockType = Bronze; Weight = 0.4<kg> }
      { RockId = %"R20"; RockType = Gold; Weight = 5.9<kg> } ]

let rockPrice: SMap2<RockId, BucketId, USD> =
    [ for rock in rocks do
        for bucket in buckets do
            (rock.RockId, bucket.BucketId),
            getRockPrice rock bucket.Rating ]
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

let ``Bronze concentration must be less than twenty percent`` =
    [ for bucket in buckets do
        sum (rockAddedToBucket)]

    

// Model

let model =
    Model.create ``Maximize total price``
    |> Model.addConstraints ``Rock can only be added to one bucket``

// Solve

let result = Solver.solve Settings.basic model

match result with
| Optimal solution ->
    printfn "Total price: $%f" (Objective.evaluate solution ``Maximize total price``)

    let rockAddedToBucketValues = Solution.getValues solution rockAddedToBucket

    printfn "rockAddedToBucketValues: %A" (rockAddedToBucketValues |> Map.toList) 

    let rockBucketPrice =
        rockAddedToBucketValues
        |> Map.filter (fun _ addedToBucket -> addedToBucket > 0.0)
        |> Map.map (fun key _ -> rockPrice.[key])

    printfn "Rock prices:\n%A" rockBucketPrice
| other -> printfn "Failed to solve: %A" other
