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
type [<Measure>] buyerId and BuyerId = string<buyerId>
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

type Buyer =
    { BuyerId: BuyerId
      Capacity: Kg
      CurrentGold: Kg
      CurrentSilver: Kg
      CurrentBronze: Kg
      Discount: float }

type Rock =
    { RockId: RockId
      RockType: RockType
      Weight: Kg }

let getRockPrice (rock:Rock) (buyer:Buyer) =
    let pricePerKg =
        match rock.RockType with
        | Gold -> 0.98<usd/kg>
        | Silver -> 0.95<usd/kg>
        | Bronze -> 0.92<usd/kg>
    pricePerKg * (1.0 - buyer.Discount) * rock.Weight

// Inputs

let maxGoldConcentration = 0.8
let maxSilverConcentration = 0.5
let maxBronzeConcentration = 0.3

let buyers =
    [ { BuyerId = %"B1"; Capacity = 40.0<kg>; CurrentGold = 5.0<kg>; CurrentSilver = 10.0<kg>; CurrentBronze = 5.0<kg>;  Discount = 0.1; }
      { BuyerId = %"B2"; Capacity = 50.0<kg>; CurrentGold = 10.0<kg>; CurrentSilver = 5.0<kg>; CurrentBronze = 5.0<kg>; Discount = 0.08 }
      { BuyerId = %"B3"; Capacity = 30.0<kg>; CurrentGold = 5.0<kg>; CurrentSilver = 0.0<kg>; CurrentBronze = 2.0<kg>; Discount = 0.0 }
      { BuyerId = %"B4"; Capacity = 10.0<kg>; CurrentGold = 1.0<kg>; CurrentSilver = 1.0<kg>; CurrentBronze = 0.5<kg>; Discount = 0.05 } ]

let buyerCapacity: SMap<BuyerId, Kg> =
    [ for buyer in buyers do
        buyer.BuyerId, buyer.Capacity ]
    |> SMap

let totalBuyerCapacity =
    buyers
    |> List.sumBy (fun b -> b.Capacity)

printfn $"Total buyer capacity: {totalBuyerCapacity}"

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

let rockPrice: SMap2<RockId, BuyerId, USD> =
    [ for rock in rocks do
        for buyer in buyers do
            (rock.RockId, buyer.BuyerId),
            getRockPrice rock buyer ]
    |> SMap2

let rockWeight: SMap2<RockId, RockType, Kg> =
    [ for rock in rocks do
        (rock.RockId, rock.RockType), rock.Weight]
    |> SMap2

// Decisions

let rockBought: SMap2<RockId, BuyerId, Decision<1>> =
    [ for rock in rocks do
        for buyer in buyers do
            let rockId, buyerId = rock.RockId, buyer.BuyerId
            (rockId, buyerId),
            Decision.createBoolean $"{rockId}_bought_by_{buyerId}" ]
    |> SMap2

// Objective

let totalPrice = sum (rockBought .* rockPrice)

let ``Maximize total price`` = Objective.create "MaximizeTotalPrice" Maximize totalPrice

// Constraints

let ``Rock can only be bought by one buyer`` =
    [ for rock in rocks do 
        Constraint.create $"{rock.RockId}_can_only_be_bought_by_one_buyer"
            (sum rockBought.[rock.RockId, All] <== 1.0 ) ]

let createConcentrationConstraint (rockType:RockType) (concentrationLimit:float) =
    [ for buyer in buyers do
        let currentRockWeight =
            match rockType with
            | Gold -> buyer.CurrentGold
            | Silver -> buyer.CurrentSilver
            | Bronze -> buyer.CurrentBronze
        let currentWeight = buyer.CurrentGold + buyer.CurrentSilver + buyer.CurrentBronze
        let rockWeightAdded = sum (rockBought.[All, buyer.BuyerId] .* rockWeight.[All, rockType])
        let weightAdded = sum (rockBought.[All, buyer.BuyerId] .* rockWeight)
        let newRockWeight = currentRockWeight + rockWeightAdded
        let newWeight = currentWeight + weightAdded
        let rockWeightLimit = concentrationLimit * newWeight
        Constraint.create $"{buyer.BuyerId}_{rockType}_concentration_below_limit" (newRockWeight <== rockWeightLimit) ]

let ``Buyer weight must be less than or equal to capacity`` =
    [ for buyer in buyers do
        let currentWeight = buyer.CurrentGold + buyer.CurrentSilver + buyer.CurrentBronze
        let weightAdded = sum (rockBought.[All, buyer.BuyerId] .* rockWeight)
        let totalWeight = currentWeight + weightAdded
        let totalCapacity = buyerCapacity.[buyer.BuyerId]
        Constraint.create $"{buyer.BuyerId}_total_weight_must_be_less_than_capacity" (totalWeight <== totalCapacity) ]

// Model

let model =
    Model.create ``Maximize total price``
    |> Model.addConstraints ``Rock can only be bought by one buyer``
    |> Model.addConstraints (createConcentrationConstraint Gold maxGoldConcentration)
    |> Model.addConstraints (createConcentrationConstraint Silver maxSilverConcentration)
    |> Model.addConstraints (createConcentrationConstraint Bronze maxBronzeConcentration)
    |> Model.addConstraints ``Buyer weight must be less than or equal to capacity``

// Solve

#time "on"
let result = Solver.solve { Settings.basic with MaxDuration = 30_000L } model
#time "off"

match result with
| Optimal solution ->
    printfn "Total price: $%f" (Objective.evaluate solution ``Maximize total price``)

    let solutionFrame =
        Solution.getValues solution rockBought
        |> Map.toList
        |> List.groupBy (fun ((rockId, _), _) -> rockId)
        |> List.map (fun (rockId, records) ->
            let values =
                records
                |> List.map (fun ((_, buyerId), value) ->
                    buyerId => if value > 0.0 then "x" else "" )
            rockId => series values)
        |> Frame.ofRows

    solutionFrame.SaveCsv(solutionPath)
| other -> printfn "Failed to solve: %A" other
