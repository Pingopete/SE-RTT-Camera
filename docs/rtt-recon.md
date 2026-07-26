# SE2 render-to-texture reconnaissance

Generated from `D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2`.

Question this answers: can a second camera render the 3D scene into an
offscreen target that we can blit onto an LCD panel?

## 1. Who creates / consumes offscreen render targets

Every method that calls `CreateOffscreenTarget`, `Borrow`, or reads
`OffscreenRenderTarget.TextureHandle`, with the other engine calls it makes
(so the surrounding recipe is visible).

**13 call sites.**

### `Game2.Client: Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdPanelSurfaceRenderComponent.TransitionToCustomRender` — borrows+reads-handle

```
  LcdMultiPanelComponent.GetSurfaceState
  LcdPanelSurfaceRenderComponent.RebuildSurfaceContent
  LcdPanelSurfaceRenderComponent.ReturnRenderTarget
  LcdPanelSurfaceContext.get_Definition
  LcdPanelSurfaceRenderComponent.IsOrientationSwapped
  Vector2I..ctor
  Component.get_Entity
  Entity.get_DebugName
  Entity.get_DEntity
  LcdRenderTargetPoolSessionComponent.Borrow
  OffscreenRenderTarget.get_TextureHandle
  LcdPanelSurfaceContext.SetNewScreenMaterialHandle
  LcdPanelSurfaceRenderComponent.UpdateMaterialReplacements
```

### `Game2.Client: Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdRenderTargetPoolSessionComponent.Borrow` — creates

```
  LcdRenderTargetPoolSessionComponent.EstimateBytes
  LcdRenderTargetPoolSessionComponent.AdjustPerResolution
  LcdRenderTargetPoolSessionComponent.PublishStats
  LcdRenderTargetPoolSessionComponent.EvictUntilFits
  RenderContracts.CreateOffscreenTarget
```

### `Game2.Client: Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdRenderTargetPoolSessionComponent.Return` — takes-rt-param

```
  LcdRenderTargetPoolSessionComponent.EstimateBytes
  LcdRenderTargetPoolSessionComponent.AdjustPerResolution
  LcdRenderTargetPoolSessionComponent.PublishStats
```

### `VRage.Render12: Keen.VRage.Render12.UIStage.OffscreenUIRenderer.DrawOne` — takes-rt-param

```
  OffscreenRenderTargetComponent.get_Handle
  OffscreenRenderTargetComponent.get_Resolution
  Profiler.Begin
  UISystemComponent.RecordBatches
  UIBatcher.BeginDraw
  OffscreenRenderTargetComponent.get_Format
  OffscreenRenderTargetComponent.get_MipLevels
  Color..ctor
  BindableTexturePoolManager.BorrowRWRenderTargetTexture
  Borrowed`1.get_Resource
  DirectCommandList.ClearRenderTargetView
  Viewport..ctor
  Vector2..ctor
  Enumerator.get_Current
  IUIBatch.Draw
  Enumerator.MoveNext
  MipMapJobExtensions.DoWork
  OffscreenRenderTargetComponent.get_Texture
  CopyCommandList.CopyResource
  BindableTexturePoolManager.Return
  UIBatcher.EndDrawKeepBuffers
  ProfilingScope.Dispose
```

### `VRage.Render12: Keen.VRage.Render12.Utils.OffscreenTargetManager.EnqueueTakingScreenshotToMemory` — takes-rt-param

```
  CoreSystems.AssertRenderThread
  OffscreenRenderTargetComponent.get_Handle
  Assert.True
```

### `VRage.Render12: Keen.VRage.Render12.Utils.OffscreenTargetManager.RegisterOffscreenTexture` — takes-rt-param

```
  CoreSystems.AssertRenderThread
  OffscreenRenderTargetComponent.get_Handle
  Assert.True
```

### `VRage.Render12: Keen.VRage.Render12.Utils.OffscreenTargetManager.TryDequeueNextRenderRequest` — takes-rt-param

```
  CoreSystems.AssertRenderThread
```

### `VRage.Render12: Keen.VRage.Render12.Utils.OffscreenTargetManager.TryDequeueWork` — takes-rt-param

```
  LoadingMonitor.get_LoadingCount
  CollectionExtensions.First
```

### `VRage.Render12: Keen.VRage.Render12.Utils.OffscreenTargetManager.UnregisterOffscreenTexture` — takes-rt-param

```
  CoreSystems.AssertRenderThread
  OffscreenRenderTargetComponent.get_Handle
  Assert.True
```

### `VRage.Render: Keen.VRage.Render.Contracts.UISystem.CreateImmediateBatchFor` — takes-rt-param

```
  Singleton`1.get_Instance
  PoolManager.Borrow
  OffscreenRenderTarget.get_Id
  GeneratedResourceHandle..ctor
  RenderDrawCommandBuffer.set_RenderTarget
  ImmediateDrawBatch.Init
```

### `VRage.Render: Keen.VRage.Render.Contracts.UISystem.CreatePersistentBatchFor` — takes-rt-param

```
  Singleton`1.get_Instance
  PoolManager.Borrow
  OffscreenRenderTarget.get_Id
  GeneratedResourceHandle..ctor
  RenderDrawCommandBuffer.set_RenderTarget
  IDrawBatch.get_CommandBuffer
  PersistentDrawBatch.Init
```

### `VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputManager.add_OnScreenshotToMemoryTaken` — takes-rt-param

```
```

### `VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputManager.remove_OnScreenshotToMemoryTaken` — takes-rt-param

```
```

## 2. Offscreen target in signatures, fields and properties

```
field  Keen.VRage.Render.OutputContracts.RenderOutputManager.OnScreenshotToMemoryTaken : Action`4
method Keen.VRage.Render.OutputContracts.RenderOutputManager.add_OnScreenshotToMemoryTaken(Action`4 value) -> Void
method Keen.VRage.Render.OutputContracts.RenderOutputManager.remove_OnScreenshotToMemoryTaken(Action`4 value) -> Void
method Keen.VRage.Render.Contracts.RenderContracts.CreateOffscreenTarget(String name, Vector2I resolution) -> OffscreenRenderTarget
method Keen.VRage.Render.Contracts.UISystem.CreateImmediateBatchFor(Nullable`1 renderTarget, Int32 sortLayer, String debugName) -> ImmediateDrawBatch
method Keen.VRage.Render.Contracts.UISystem.CreatePersistentBatchFor(Nullable`1 renderTarget, Int32 sortLayer, IDrawBatch previousBatch, Boolean deletePrevious) -> PersistentDrawBatch
field  Keen.VRage.Render12.Utils.OffscreenTargetManager._registeredTextures : Dictionary`2
field  Keen.VRage.Render12.Utils.OffscreenTargetManager._immediatelyScreenshotsToMemory : HashSet`1
field  Keen.VRage.Render12.Utils.OffscreenTargetManager._fullyLoadedScreenshotsToMemory : HashSet`1
method Keen.VRage.Render12.Utils.OffscreenTargetManager.RegisterOffscreenTexture(OffscreenRenderTargetComponent offscreenRenderTarget) -> Void
method Keen.VRage.Render12.Utils.OffscreenTargetManager.UnregisterOffscreenTexture(OffscreenRenderTargetComponent offscreenRenderTarget) -> Void
method Keen.VRage.Render12.Utils.OffscreenTargetManager.TryDequeueNextRenderRequest(OffscreenRenderTargetComponent& component) -> Boolean
method Keen.VRage.Render12.Utils.OffscreenTargetManager.EnqueueTakingScreenshotToMemory(OffscreenRenderTargetComponent offscreenRenderTarget, Boolean waitUtilFullyLoaded) -> Void
method Keen.VRage.Render12.Utils.OffscreenTargetManager.TryDequeueWork(OffscreenRenderTargetComponent& offscreenTexture) -> Boolean
method Keen.VRage.Render12.UIStage.OffscreenUIRenderer.DrawOne(DirectCommandList commandList, UISystemComponent uiSystem, OffscreenRenderTargetComponent target) -> Void
field  Keen.VRage.Render12.SceneSystem.Builders.EntityBuilders.OffscreenRenderTargetBuilder : OffscreenRenderTargetBuilder
field  Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdPanelSurfaceContext.RenderTarget : Nullable`1
field  Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdRenderTargetPoolSessionComponent._buckets : Dictionary`2
method Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdRenderTargetPoolSessionComponent.Borrow(String debugName, Vector2I resolution) -> OffscreenRenderTarget
method Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdRenderTargetPoolSessionComponent.Return(OffscreenRenderTarget rt, Vector2I resolution) -> Void
```

20 entries.

## 3. Camera types and the render camera path

**175 camera-named types.**

```
Game2.Client       pub Keen.Game2.Client.Debugging.Screens.Camera.CameraDebugScreen
Game2.Client       int Keen.Game2.Client.Debugging.Screens.Camera.CameraDebugScreen/OnOverrideCameraPosition
Game2.Client       int Keen.Game2.Client.Debugging.Screens.Entities.EntityDebugScreen/CameraChangeTag
Game2.Client       int Keen.Game2.Client.Debugging.Screens.Entities.EntityDebugScreen/ChangeCameraJobGroup
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Adapters.IFirstPersonCameraAdapter
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Adapters.IThirdPersonCameraAdapter
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent/OnCameraTranformOverride
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent/OverrideCameraTransform
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerChildComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerChildComponent/AttachedCameraControllerData
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerChildComponent/CameraAdjustedWorldTransformData
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerChildComponent/CameraResetLookOffsetTag
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneComponent/CameraAttachToLocalPlayerTag
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneComponent/StandaloneCameraData
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneComponent/UpdateCameraJob
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerStandaloneDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerUpdate
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerUpdate/CameraControllerUpdateJobGroup
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraData
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDebrisCleanerComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDebrisCleanerDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDebrisCleanerDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDebrisCleanerDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraDefinitionObjectBuilder_Migrations
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraEffectExclusionComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraMovementEffectsComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraMovementEffectsComponent/UpdateCameraMovementEffect
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraMovementEffectsDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraMovementEffectsDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraMovementEffectsDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraOptions
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraOptions_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeArgs
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeComponentDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeComponentDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeComponentDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeSettingsDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeSettingsDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraShakeSettingsDefinitionObjectBuilder_Migrations
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSpaceEffects.CameraSpaceEffectHelper
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSpaceEffects.KillingFieldCameraEffectComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSpaceEffects.KillingFieldCameraEffectComponent/UpdateCameraEffect
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSpaceEffects.KillingFieldCameraEffectDefinition
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSpaceEffects.KillingFieldCameraEffectDefinitionObjectBuilder
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSpaceEffects.KillingFieldCameraEffectDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSwayArgs
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSwayComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSwayComponentDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSwayComponentDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSwayComponentDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent/CameraNeedsUpdateTag
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent/CameraStateSync
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemObjectBuilder_Migrations
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraUpdateSystem
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraUpdateSystem/CameraControllerInputUpdate
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraUpdateSystem/CameraLookOffsetUpdate
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraUpdateSystem/CameraSpringUpdate
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.CameraUpdateSystem/SwapCameraUpdate
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Helpers.CameraControllerHelper
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Helpers.CameraInputHelper
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.ICameraSystem
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.ICameraSystem/OnCameraChangedSignal
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.ICameraSystem/__InternalOnCameraChangedSignal
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.ISwapAwareCameraController
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.CameraViewMode
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraComponent/FirstPersonCameraSpringData
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraComponent/FollowCameraBoneJob
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraEntityComponentDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraEntityComponentDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraEntityComponentDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraWithInputComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraWithInputDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraWithInputDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraWithInputDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.FreeMoveCameraComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.ICameraViewModeListener
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/CameraCollisionDetectionUpdate
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/CameraControllerEnabledChangedJobGroup
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/CameraDistanceNeedsUpdateTag
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/CameraLookOffsetRemoved
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/DisableCameraSpringTag
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/ThirdPersonCameraData
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraComponent/ThirdPersonCameraSpringData
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Modes.ThirdPersonCameraDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.NamedCameraModesDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.NamedCameraModesDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.NamedCameraModesDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.RenderCameraUpdate
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeComponent/CameraShakeJob
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.AccelerationCameraShakeDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.BaseCameraShakeControllerComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Shake.BaseCameraShakeControllerComponent/AfterCameraUpdate
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.Shake.BaseCameraShakeControllerComponent/BeforeCameraShakes
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.CameraShakeData
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.CameraShakeEntityExtensions
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.CameraShakeObserverKey
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.CharacterCameraShakeControllerComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.Shake.GridCameraShakeControllerComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.WeaponCameraShakeComponent
Game2.Client       int Keen.Game2.Client.GameSystems.CameraSystems.WeaponCameraShakeComponent/CameraShakeJob
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.WeaponCameraShakeDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.WeaponCameraShakeDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.CameraSystems.WeaponCameraShakeDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.Weather.CameraWeatherEffectsComponent
Game2.Client       pub Keen.Game2.Client.GameSystems.Weather.CameraWeatherEffectsDefinition
Game2.Client       pub Keen.Game2.Client.GameSystems.Weather.CameraWeatherEffectsDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.GameSystems.Weather.CameraWeatherEffectsDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.GameSystems.Weather.ICameraWeatherEffectsSource
Game2.Client       pub Keen.Game2.Client.RuntimeSystems.Analytics.SessionComponents.CameraAnalyticsSessionComponent
Game2.Client       int Keen.Game2.Client.RuntimeSystems.Analytics.SessionComponents.CameraAnalyticsSessionComponent/OnSetCameraAdapterSignal
Game2.Client       int Keen.Game2.Client.RuntimeSystems.Analytics.SessionComponents.CameraAnalyticsSessionComponent/__InternalOnSetCameraAdapterSignal
Game2.Client       pub Keen.Game2.Client.WorldObjects.Character.CharacterFirstPersonFixedCameraAnimatorStrategyComponent
Game2.Client       int Keen.Game2.Client.WorldObjects.Character.CharacterFirstPersonFixedCameraAnimatorStrategyComponent/CameraBonePositions
Game2.Client       int Keen.Game2.Client.WorldObjects.Character.CharacterFirstPersonFixedCameraAnimatorStrategyComponent/CollectCameraBonePositionJob
Game2.Client       pub Keen.Game2.Client.WorldObjects.Character.CharacterFirstPersonFixedCameraAnimatorStrategyDefinition
Game2.Client       pub Keen.Game2.Client.WorldObjects.Character.CharacterFirstPersonFixedCameraAnimatorStrategyDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.WorldObjects.Character.CharacterFirstPersonFixedCameraAnimatorStrategyDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.WorldObjects.Character.FirstPersonModeRenderCameraAdapterComponent
Game2.Client       int Keen.Game2.Client.WorldObjects.Character.FirstPersonModeRenderCharacterAdapterComponent/CameraModeChangesSynchronizer
Game2.Client       int Keen.Game2.Client.WorldObjects.ColonizationMap.ColonizationMapSessionComponent/CameraData
Game2.Client       int Keen.Game2.Client.WorldObjects.ColonizationMap.ColonizationMapSessionComponent/CameraNeedsUpdateTag
Game2.Client       pub Keen.Game2.Client.WorldObjects.Shared.Render.Drills.DrillCameraShakeComponent
Game2.Client       int Keen.Game2.Client.WorldObjects.Shared.Render.Drills.DrillCameraShakeComponent/CameraShakeJob
Game2.Client       pub Keen.Game2.Client.WorldObjects.Shared.Render.Drills.DrillCameraShakeDefinition
Game2.Client       pub Keen.Game2.Client.WorldObjects.Shared.Render.Drills.DrillCameraShakeDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.WorldObjects.Shared.Render.Drills.DrillCameraShakeDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.GunToolCameraADSComponent
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.GunToolCameraADSDefinition
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.GunToolCameraADSDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.GunToolCameraADSDefinitionObjectBuilder_Migrations
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.ToolCameraShakeComponent
Game2.Client       int Keen.Game2.Client.WorldObjects.Tools.ToolCameraShakeComponent/CameraShakeJob
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.ToolCameraShakeDefinition
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.ToolCameraShakeDefinitionObjectBuilder
Game2.Client       pub Keen.Game2.Client.WorldObjects.Tools.ToolCameraShakeDefinitionObjectBuilder_Migrations
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.CameraPerPlayerData
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.CameraPerPlayerData_Migrations
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.CameraStatePerPlayerData
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.CameraStatePerPlayerData_Migrations
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.FirstPersonCameraObjectBuilder
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.FirstPersonCameraObjectBuilder_Migrations
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.FirstPersonCameraWithInputObjectBuilder
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.FirstPersonCameraWithInputObjectBuilder_Migrations
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.OffcenterCameraPosition
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.ThirdPersonCameraObjectBuilder
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.Camera.ThirdPersonCameraObjectBuilder_Migrations
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.CameraShake.CameraShakeConfiguration
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.CameraShake.CameraShakeConfigurationObjectBuilder
Game2.Simulation   pub Keen.Game2.Simulation.GameSystems.CameraShake.CameraShakeConfigurationObjectBuilder_Migrations
VRage.Core         int Keen.VRage.Core.Systems.PostAppUpdateSystem/CameraTransformJob
VRage.Render       int Keen.VRage.Render.Data.VolumetricWaterSettings/HighFOVCameraData
VRage.Render12     int Keen.VRage.Render12.Primitives.Frame.CameraSettings
VRage.Render12     int Keen.VRage.Render12.Primitives.Frame.CameraSettings/CameraFlagBits
VRage.Render12     int Keen.VRage.Render12.Primitives.Frame.PreviousCameraSettings
VRage.Render12     int Keen.VRage.Render12.Primitives.Frame.TrackedCameraSettings
VRage.Render12     int Keen.VRage.Render12.SceneSystem.Jobs.RenderUpdateOrder/Flora/UpdateCamera
```

### `Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent`

```
  int field Single <AspectRatio>k__BackingField
  pub field CameraDefinition Definition
  int field WorldTransformComponent _positionEntityComponent
  int field CameraShakeComponent _cameraShakeComponent
  int field CameraSwayComponent _cameraSwayComponent
  int field RenderContracts _renderContracts
  int field IOptions _options
  int field Single _defaultFov
  int field Nullable`1 _customFov
  int field Vector2I _resolution
  int field Boolean _isNextTransitionSmooth
  int field RenderDisplayOptionsPart _renderDisplayOptions
  int field RenderOptionsPart2 _renderOptions
  int field MatrixD <ViewProjectionMatrix>k__BackingField
  int field Matrix <ProjectionMatrix>k__BackingField
  int field StringId ___JobId__Update
  int field StringId ___JobId__OverrideCamera
  prop  Single AspectRatio
  prop  Single FieldOfView
  prop  Vector2I Resolution
  prop  Single NearPlane
  prop  MatrixD ViewProjectionMatrix
  prop  Matrix ProjectionMatrix
  int Void Init()
  int Void Destroy()
  pub Void SetCustomFOV(Single)
  pub Void ResetCustomFOV()
  pub Single ToScreenSpace(Single)
  pub Vector3 GetNearPlaneHalfExtents()
  pub Vector3 WorldToProjected(Vector3D&)
  pub Vector2 ProjectedToScreenPointNormalized(Vector3)
  pub Vector2 WorldToScreenPoint(Vector3D&)
  pub Vector3D ScreenToWorldPoint(Vector2&, Single&)
  pub Vector2 NormalizedScreenToScreenPoint(Vector2)
  pub Void SetNextTransitionNonSmooth()
  int Void Update()
  int Void OnRenderOptionsChanged(Object, PropertyChangedEventArgs)
  int Matrix CreateProjectionMatrixWithFov(Single)
  int Void UpdateRenderSettingsInternal(WorldTransform&)
  pub Void UpdateRenderSettings()
  pub Void SetTransformOverride(Nullable`1)
  int Void OverrideCamera(OverrideCameraTransform&)
  int Void Update_InvocationStub(Byte**, Int32, Scene, Object)
  int Void OverrideCamera_InvocationStub(Byte**, Int32, Scene, Object)
  pub Void .ctor()
  int Void .cctor()
```

### `RenderCameraComponent` — not found

### `Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent`

```
  int field String CAMERA_STATE_TAG
  int field Entity _newEntityToObserve
  int field CameraControllerHelper _activeCamera
  int field Entity <ObservedEntity>k__BackingField
  int field Entity <RenderCameraEntity>k__BackingField
  int field CameraComponent _renderCameraComponent
  int field PlayerControllerComponent _playerController
  int field CameraSystemDefinition _definition
  int field IInputProcessor _inputProcessor
  int field IPerPlayerData _perPlayerData
  int field ClientPlayersSessionComponent _players
  int field RenderContracts _renderContracts
  int field IOptions _options
  int field Dictionary`2 _rememberedEntityCameraStates
  int field Dictionary`2 _rememberedEntityNamedCameraStates
  int field EntityObjectBuilder _activeCameraMode
  int field InputContext _inputContext
  int field CameraOptions _cameraOptions
  int field OffcenterCameraPosition _offcenterPosition
  int field OffcenterCameraPosition _lastOffcenterPosition
  int field StringId ___JobId__UpdateCameraControl
  int field StringId ___JobId__StoreCameraData
  pub field Int32 __signalTableOffset
  prop  Entity ObservedEntity
  prop  Entity RenderCameraEntity
  prop  Entity ActiveCameraController
  prop  Nullable`1 ActiveCameraModeIndex
  int Void Init(CameraSystemObjectBuilder)
  int Void Keen.VRage.Core.Game.Systems.IInSceneListener.OnBeforeRemovedFromScene()
  int Void OnCameraChanged(Entity)
  int Void UpdateCameraControl()
  pub Boolean CanEntityBeObserved(Entity)
  pub Void ResetCameraModeForEntity(Entity)
  pub Boolean TrySetCameraModeForEntity(Entity, KeyDefinition)
  pub Void EnableCameraInput(Boolean)
  pub Boolean TrySetCameraModeForEntity(Entity, Int32, Object)
  int Void SetCameraData(EntityObjectBuilder, Object)
  int Boolean TestMatchingObType(Object, Object)
  int Object GetCameraData(EntityObjectBuilder)
  int Object GetCameraData(Entity)
  int Void GiveCameraControlTo(Entity, Boolean)
  int Void OnObservedEntityRemoved(Entity)
  int Void OnCameraReleased(IObservableDisposable)
  int Void StoreCamera()
  int Void TrySyncCameraData()
  int Void ResetCamera(Boolean)
  pub Void RequestCameraChange(Entity)
  pub Task WaitForCameraChange(Entity)
  pub Boolean RequestSpecificCamera(KeyDefinition)
  int Void GetNewCamera()
  int InputContext PrepareInputContext()
  int Void ToggleCameraView()
  int Void SwitchShoulderOffset()
  int Void StoreCameraEntityAndIndex(Definition)
  int Void SetStoredCameraIndex(Entity, Nullable`1, Definition)
  int Void StoreCameraData()
  int Void UpdateCameraControl_InvocationStub(Byte**, Int32, Scene, Object)
  int Void StoreCameraData_InvocationStub(Byte**, Int32, Scene, Object)
  pub Void .ctor()
  int Void .cctor()
```

### `Keen.VRage.Render.Contracts.MainRenderTarget`

```
  pub Task TakeScreenshotAsync(FileHandleWritable, Nullable`1, Nullable`1, Boolean, Boolean)
  int Void TakeScreenshot_Impl(FileHandleWritable, TaskCompletionSource, Nullable`1, Nullable`1, Boolean, Boolean)
  pub Void .ctor()
```

### `Keen.VRage.Render.Contracts.RenderSettings`

```
  int field AtmosphereSettings _atmosphereSettings
  int field CloudSettings _cloud
  int field DebugSettings _debugSettings
  int field DecalSettings _decalSettings
  int field DRSSettings _drsSettings
  int field EnvironmentSettings _environment
  int field GrassSettings _grassSettings
  int field TerrainSettings _terrainSettings
  int field FrameManagerSettings _frameManagerSettings
  int field HBAOSettings _hbaoSettings
  int field HologramSettings _hologramSettings
  int field HZBOSettings _hzboSettings
  int field LightSettings _light
  int field LODSettings _lod
  int field MeshEffectSettings _meshEffectSettings
  int field OverridesSettings _overrides
  int field ParallaxSettings _parallaxSettings
  int field PostProcessSettings _postProcessSettings
  int field RaytracingSettings _raytracingSettings
  int field ShadowSettings _shadow
  int field SSSRSettings _sssrSettings
  int field StreamingSettings _streamingSettings
  int field SystemSettings _systemSettings
  int field VolumetricWaterSettings _waterSettings
  int field WindSettings _windSettings
  int field GPUParticlesSettings _particlesSettings
  int field FloraSettings _floraSettings
  int field RenderingDistanceSettings _renderingDistanceSettings
  int field ImpostorSettings _impostorSettings
  int field InternalStateSettings _internalStateSettings
  int field WeatherSettings _weatherSettings
  int field SnowSettings _snowSettings
  int field WorldTransform <CameraTransform>k__BackingField
  int field Single _defaultFov
  int field Single _fov
  prop  WorldTransform CameraTransform
  prop  Single CameraFov
  prop  AtmosphereSettings AtmosphereSettings
  prop  CloudSettings CloudSettings
  prop  DebugSettings DebugSettings
  prop  DecalSettings DecalSettings
  prop  DRSSettings DRSSettings
  prop  EnvironmentSettings EnvironmentSettings
  prop  GrassSettings GrassSettings
  prop  TerrainSettings TerrainSettings
  prop  FrameManagerSettings FrameManagerSettings
  prop  HBAOSettings HBAOSettings
  prop  HologramSettings HologramSettings
  prop  HZBOSettings HZBOSettings
  prop  LightSettings LightSettings
  prop  SunSettings SunlightSettings
  prop  LODSettings LODSettings
  prop  MeshEffectSettings MeshEffectSettings
  prop  OverridesSettings OverridesSettings
  prop  ParallaxSettings ParallaxSettings
  prop  PostProcessSettings PostProcessSettings
  prop  RaytracingSettings RaytracingSettings
  prop  ShadowSettings ShadowSettings
  prop  SSSRSettings SSSRSettings
  prop  StreamingSettings StreamingSettings
  prop  SystemSettings SystemSettings
  prop  VolumetricWaterSettings WaterSettings
  prop  WindSettings WindSettings
  prop  GPUParticlesSettings GPUParticlesSettings
  prop  FloraSettings FloraSettings
  prop  RenderingDistanceSettings RenderingDistanceSettings
  prop  ImpostorSettings ImpostorSettings
  prop  InternalStateSettings InternalStateSettings
  prop  WeatherSettings WeatherSettings
  prop  SnowSettings SnowSettings
  int Void .ctor()
  pub Void SetCameraParameters(WorldTransform&, Single, Single, Single, Single, Vector2, Boolean, Boolean)
  int Void SetCameraParameters_Impl(WorldTransform&, Single, Single, Single, Single, Vector2, Boolean, Boolean)
  pub Void SetDefaultFieldOfView(Single)
  int Void SetDefaultFieldOfView_Impl(Single)
  pub Void SetCharacterPerspectiveMode(Boolean)
  pub Void SetLocalEnvironmentRoot(Nullable`1, Nullable`1)
  pub Void SetLocalShadowsStreamingRoot(Nullable`1, Vector3)
  pub Void SetCharacterShadowTarget(ModelEntity, Boolean, CharacterShadowType)
  int Void SetLocalEnvironmentRoot_Impl(Nullable`1, Nullable`1)
  int Void SetCharacterPerspectiveMode_Impl(Boolean)
  int Void SetAtmosphereSettings_Impl(AtmosphereSettings&)
  int Void SetCloudSettings_Impl(CloudSettings&)
  int Void SetDebugSettings_Impl(DebugSettings&)
  int Void SetDecalSettings_Impl(DecalSettings&)
  int Void SetEnvironmentSettings_Impl(EnvironmentSettings&)
  int Void SetGrassSettings_Impl(GrassSettings&)
  int Void SetTerrainSettings_Impl(TerrainSettings&)
  int Void SetFrameManagerSettings_Impl(FrameManagerSettings&)
  int Void SetHBAOSettings_Impl(HBAOSettings&)
  int Void SetHologramSettings_Impl(HologramSettings&)
  int Void SetLightSettings_Impl(LightSettings&)
  int Void SetSunlightSettings_Impl(SunSettings&)
  int Void SetLODSettings_Impl(LODSettings&)
  int Void SetMeshEffectSettings_Impl(MeshEffectSettings&)
  int Void SetPostProcessSettings_Impl(PostProcessSettings&)
  int Void SetRaytracingSettings_Impl(RaytracingSettings&)
  int Void SetShadowSettings_Impl(ShadowSettings&)
  int Void SetSSSRSettings_Impl(SSSRSettings&)
  int Void SetStreamingSettings_Impl(StreamingSettings&)
  int Void SetSystemSettings_Impl(SystemSettings&)
  int Void SetDRSSettings_Impl(DRSSettings&)
  int Void SetHZBOSettings_Impl(HZBOSettings&)
  int Void SetOverridesSettings_Impl(OverridesSettings&)
  int Void SetParallaxSettings_Impl(ParallaxSettings&)
  int Void SetWaterSettings_Impl(VolumetricWaterSettings&)
  int Void SetWindSettings_Impl(WindSettings&)
  int Void SetGPUParticlesSettings_Impl(GPUParticlesSettings&)
  int Void SetFloraSettings_Impl(FloraSettings&)
  int Void SetRenderingDistanceSettings_Impl(RenderingDistanceSettings&)
  int Void SetImpostorSettings_Impl(ImpostorSettings&)
  int Void SetLocalShadowsStreamingRoot_Impl(Nullable`1, Vector3)
  int Void SetCharacterShadowTarget_Impl(ModelEntity, Boolean, CharacterShadowType)
  int Void SetInternalStateSettings_Impl(InternalStateSettings&)
  int Void SetWeatherSettings_Impl(WeatherSettings&)
  int Void SetSnowSettings_Impl(SnowSettings&)
  int Void EnsureIsUnmanaged(T&)
```

## 4. RenderSystem / render contract surface

### `Keen.VRage.Render.Contracts.RenderSystem`

```
  prop Boolean AreDebugCommandsEnabled
  prop AdapterInfo Adapter
  prop ImmutableArray`1 AllAdapters
  prop RenderObjectBuilder Settings
  prop RenderDisplaySettings DisplaySettings
  pub Void SetDisplaySettings(RenderDisplaySettings& settings)
  pub Void SetFacadeDisplaySettings(RenderDisplaySettings& settings)
  pub Void SetDrawUI(Boolean enable)
  pub Void SetDraw3DScene(Boolean enable)
  pub Void SetDraw3DMap(Boolean enable)
  pub Void SetFixedFrameTimeDelta(TimeSpan fixedFrameTimeDelta)
  pub Void ResetRandomness(Nullable`1 seed)
  pub Void PauseTimer(RenderTimerType timer, Boolean pause)
  pub Void SendDebugCommand(DebugCommandType command)
  int Void SendDebugCommand_Impl(DebugCommandType command)
  pub Void ResetRenderContext()
  pub Task Fence()
  int Void Fence_Impl(TaskCompletionSource tcs)
  pub Task EndOfLoadingFence()
  int Void EndOfLoadingFence_Impl(TaskCompletionSource tcs)
  pub RenderCommandBuffer OpenRenderCommandBuffer()
  pub Task SubmitRenderCommandBufferAsync(RenderCommandBuffer rcb)
  pub Void ForceFinishRenderCommandBuffer(RenderCommandBuffer rcb)
  int Void SubmitRenderCommandBufferAsync_Impl(RenderCommandBuffer rcb, TaskCompletionSource tcs)
```

### `Keen.VRage.Render.Contracts.RenderContracts`

```
  pub RenderSystem GetRenderSystem()
  pub DecalSystem GetDecalSystem()
  pub ParticleSystem GetParticleSystem()
  pub FloraSystem GetFloraSystem()
  pub MeshEffectSystem GetMeshEffectSystem()
  pub MaterialSystem GetMaterialSystem()
  pub UISystem GetUISystem()
  pub WaterSystem GetWaterSystem()
  pub MainRenderTarget GetMainTarget()
  pub RenderSettings GetSettings()
  pub RootEntity CreateRootEntity(String debugName, WorldTransform& worldTransform, Boolean autoActivate)
  pub VideoPlayerEntity CreateVideoPlayerEntity(ResourceHandle`1 videoHandle)
  int Void CreateVideoPlayerEntity_Impl(RenderId id, ResourceHandle`1 videoHandle, ContinuationQueue continuationQueue, IVideoPlayerEntityCallbacks callabacks)
  int Void DestroyEntity(RenderId id, Boolean fadeOut, Boolean tryDestroy)
  int Void CreateRootEntity_Impl(RenderId id, String debugName, WorldTransform& worldTransform, Boolean autoActivate)
  int T AsContract(RenderId entityId)
  pub DecalEntity CreateDecalEntity(String debugName, RelativeTransform localTransform, DecalMaterialDefinition decalMaterial, DecalEntityParentMethod parentMethod, DecalCreationParameters parameters)
  pub PlanetEnvironmentEntity CreatePlanetEnvironmentEntity(String debugName, AtmosphereDefinition atmosphereDefinition, Single atmosphereRadius, Single radiusWithMaxHills, CloudDefinition cloudDefinition, SpherizationData sphereData, SpherizationData atmosphereSpherizationData, SpherizationData skyboxSpherizationData, Single spherizeRadius, Vector3D planetCenter, PlanetOverlayDefinition planetOverlayDefinition, ResourceHandle`1[] preloadItems)
  int Void CreatePlanetEnvironmentEntity_Impl(RenderId id, String debugName, AtmosphereDefinition atmosphereDefinition, Single atmosphereRadius, Single radiusWithMaxHills, CloudDefinition cloudDefinition, SpherizationData sphereData, SpherizationData atmosphereSphereData, SpherizationData skyboxSpherizationData, Single spherizeRadius, Vector3D planetCenter, PlanetOverlayDefinition planetOverlayDefinition, ResourceHandle`1[] preloadItems)
  pub WeatherModifierEntity CreateWeatherModifierEntity(String debugName, RenderId planetEnvEntityId, WeatherModifierParameters& parameters)
  int Void CreateWeatherModifierEntity_Impl(RenderId id, String debugName, RenderId planetEnvEntityId, WeatherModifierParameters& parameters)
  pub FloraSectorEntity CreateFloraSector(String debugName, RootEntity rootEntity, Buffer`1 floraInstances, WorldTransform planetTransform)
  pub GrassEntity CreateGrassEntityForVoxel(String debugName, RootEntity rootEntity, ResourceHandle modelResourceHandle, RelativeTransform localTransform, Buffer`1 grassMaterialsUsed, Int32 lod, Boolean showImmediately)
  pub Void UpdateGrassMaterialsArray(ImmutableArray`1 grassMaterialDefinition)
  pub Void SetGrassWindProjection(Nullable`1 projectionInfo)
  int Void CreateGrassEntityForVoxel_Impl(RenderId renderEntityId, String name, RenderId parentEntityId, ResourceHandle modelResourceHandle, RelativeTransform localTransform, Buffer`1 grassMaterialsUsed, Int32 lod, Boolean showImmediately)
  int Void UpdateGrassMaterialsArray_Impl(ImmutableArray`1 grassMaterialDefinition)
  int Void SetGrassWindProjection_Impl(Nullable`1 projectionInfo)
  pub GravityProbeRenderEntity CreateGravityProbeRenderEntity(String debugName)
  int Void CreateGravityProbeRenderEntity_Impl(RenderId renderEntityId, String debugName)
  pub PointLightEntity CreatePointLightEntity(String debugName, ColorLinear lightIntensityRGB, RelativeTransform localLightTransform, Nullable`1 rootEntity, Nullable`1 localFlareTransform, FlareDefinition flareDefinition, Single glossinessMultiplier, Single falloffMultiplier, Boolean castShadows)
  pub SpotLightEntity CreateSpotLightEntity(String debugName, ColorLinear lightIntensityRGB, Single outerConeAngle, RelativeTransform localLightTransform, Nullable`1 rootEntity, Nullable`1 localFlareTransform, ResourceHandle cookieTexture, FlareDefinition flareDefinition, Boolean tryForceShadowMapAlwaysAllocated, Single falloffMultiplier, Single outerConeStartRadius)
  pub CapsuleLightEntity CreateCapsuleLightEntity(String debugName, ColorLinear lightIntensityRGB, Single lineLength, RelativeTransform localTransform, Single radius, Nullable`1 rootEntity)
  pub AreaLightEntity CreateAreaLightEntity(String debugName, ColorLinear lightIntensityRGB, Vector2 dimensions, Single barnAngle, Single barnLength, RelativeTransform localTransform, Nullable`1 rootEntity, ResourceHandle imageTexture)
  int Void CreatePointLightEntity_Impl(RenderId id, String debugName, ColorLinear lightIntensityRGB, Single FalloffRangeMultiplier, RelativeTransform localLightTransform, Nullable`1 rootEntity, Nullable`1 localFlareTransform, FlareDefinition flareDefinition, Single glossinessFactor, Boolean castShadows)
  int Void CreateSpotLightEntity_Impl(RenderId id, String debugName, ColorLinear lightIntensityRGB, Single FalloffRangeMultiplier, Single outerConeAngle, Single outerConeStartRadius, RelativeTransform localLightTransform, Nullable`1 rootEntity, Nullable`1 localFlareTransform, FlareDefinition flareDefinition, Boolean tryForceShadowMapAlwaysAllocated, ResourceHandle cookieTexture)
  int Void CreateCapsuleLightEntity_Impl(RenderId id, String debugName, ColorLinear lightIntensityRGB, Single lineLength, Single radius, RelativeTransform localTransform, Nullable`1 rootEntity)
  int Void CreateAreaLightEntity_Impl(RenderId id, String debugName, ColorLinear lightIntensityRGB, Vector2 dimensions, Single barnAngle, Single barnLength, RelativeTransform localTransform, Nullable`1 rootEntity, ResourceHandle imageTexture)
  pub ModelEntity CreateModelEntity(String debugName, ResourceHandle model, RelativeTransform localTransform, Nullable`1 rootEntity, RenderFlags flags, EntityType type, Nullable`1 planet)
  pub InstancedModelEntity CreateInstancedModelEntity(String debugName, ResourceHandle model, RelativeTransform localTransform, GeneratedResourceHandle instanceData, BoundingBox boundingBox, Nullable`1 rootEntity, RenderFlags flags, Boolean implicitLifetime)
  int MeshEffectHandle AllocateMeshEffectHandle()
  int Void CreateModelEntity_Impl(RenderId id, String debugName, ResourceHandle model, RelativeTransform localTransform, Nullable`1 rootEntity, RenderFlags flags, EntityType type, Nullable`1 planet)
  int Void CreateInstancedModelEntity_Impl(RenderId id, String debugName, ResourceHandle model, RelativeTransform localTransform, GeneratedResourceHandle instanceData, BoundingBox boundingBox, Nullable`1 rootEntity, RenderFlags flags, Boolean implicitLifetime)
  pub ParticleEffectEntity CreateParticleEffectEntity(String debugName, WorldTransform localTransform, ParticleEffectDefinition particleEffectDefinition, ParticleEffectUserParameters userParams, RootEntity rootEntity, EntityType entityType)
  pub ParticleEffectEntity CreateEmptyPooledParticleEffectEntity(String debugName)
  pub Void DestroyParticleEffect(RenderId id, Boolean tryDestroy)
  pub ParticleEffectEntity CreatePreviewParticleEffectEntity(String debugName, WorldTransform localTransform, ParticleEffectDefinition particleEffectDefinition, ParticleEffectUserParameters userParams, RootEntity rootEntity)
  pub ModelParticleEffectEntity CreateModelParticleEffectEntity(String debugName, RelativeTransform localToModelTransform, ParticleEffectDefinition particleEffectDefinition, ParticleEffectUserParameters userParams, DEntity modelEntity, Nullable`1 boneIndex)
  pub Void SetParticleKillZone(BoundingBoxD boundingBox)
  int Void CreateModelParticleEffectEntity_Impl(RenderId id, String debugName, RelativeTransform localToModelTransform, ParticleEffectDefinition particleEffectDefinition, ParticleEffectUserParameters userParams, DEntity modelEntity, Nullable`1 boneIndex, Boolean implicitLifetime)
  int Void SetParticleKillZone_Impl(BoundingBoxD boundingBox)
  pub RuntimeModel CreateRuntimeModel(IRuntimeMeshData meshData, RenderRuntimeDataType runtimeDataType, Boolean immediateUpload, Boolean prepareBLAS)
  pub RuntimeModel CreateRuntimeModel(Buffer`1 lodData, RenderRuntimeDataType runtimeDataType, Boolean immediateUpload, Boolean prepareBLAS)
  pub RuntimeBuffer CreateUnmanagedRuntimeBuffer(String debugName, ReadOnlySpan`1 bufferData, RenderRuntimeDataType runtimeDataType)
  pub RuntimeBuffer CreateRuntimeBuffer(String debugName, ReadOnlySpan`1 bufferData, RenderRuntimeDataType runtimeDataType)
  int Void DestroyRuntimeBuffer(RenderId id)
  pub RuntimeTexture3D CreateRuntimeTexture3D(IRuntimeTextureData textureData)
  pub OffscreenRenderTarget CreateOffscreenTarget(String name, Vector2I resolution)
  pub ModelResourcePin CreateModelResourcePin(Buffer`1 handles)
  pub ResourcePin CreateTextureResourcePin(Buffer`1 handles, ResourcePinType type, TextureResourcePinDimension dimension, String debugTag)
  pub ResourcePin CreateAssetResourcePin(Buffer`1 handles, String debugTag)
  pub Void UpdateCloudDefinitions(ImmutableArray`1 cloudDefinitions)
  pub Void UpdateAtmosphereDefinitions(ImmutableArray`1 atmosphereDefinitions)
  pub Void UpdateFlareDefinitions(ImmutableArray`1 flareDefinitions)
  pub Void UpdateParticleEffectDefinitions(ImmutableArray`1 particleEffectDefinitions)
  pub Void UpdateParticleEmitterDefinitions(ImmutableArray`1 particleEmitterDefinitions)
  pub Void UpdateWindAnimationDefinitions(ImmutableArray`1 windAnimationDefinitions)
  pub Void UpdateModelGroups(PooledMap`2 modelMap)
  pub Void OverrideTextureStreamingBehaviour(Nullable`1 overriddenTextureStreamingBehaviour)
  pub Void DisableTextureCachingForSingleFrame()
  pub Void SetFloraSystemTaskDeadline(Nullable`1 taskDeadline)
  pub Task`1 CaptureVideoMemorySnapshot()
  pub Task`1 CollectTextureStreamingSnapshot()
  pub Void SetTextureStreamingOverride(AssetSnapshotBase textureStreamingSnapshot)
  pub Void ResetTextureStreamingOverride()
  int Void CreateOffscreenTexture_Impl(RenderId id, String name, Vector2I resolution)
  int Void CreateRuntimeModel_Impl(RenderId id, IRuntimeMeshData meshData, RenderRuntimeDataType runtimeDataType, Boolean immediateUpload, Boolean prepareBLAS)
  int Void CreateRuntimeModelWithLODs_Impl(RenderId id, Buffer`1 lodData, RenderRuntimeDataType runtimeDataType, Boolean immediateUpload, Boolean prepareBLAS)
  int Void CreateRuntimeBuffer_Impl(RenderId id, String debugName, CustomGPUDataPayload bufferData, RenderRuntimeDataType runtimeDataType)
  int Void DestroyRuntimeBuffer_Impl(RenderId id)
  int Void CreateRuntimeTexture3D_Impl(RenderId id, IRuntimeTextureData textureData)
  int Void CreateModelResourcePin_Impl(RenderId id, Buffer`1 handles)
  int Void CreateTextureResourcePin_Impl(RenderId id, Buffer`1 handles, ResourcePinType type, TextureResourcePinDimension dimension, String debugTag)
  int Void CreateAssetResourcePin_Impl(RenderId id, Buffer`1 handles, String debugTag)
  int Void CaptureVideoMemorySnapshot_Impl(TaskCompletionSource`1 tcs)
  int Void CollectTextureStreamingSnapshot_Impl(TaskCompletionSource`1 textureStreamingSnapshotRequest)
  pub WaterRenderEntity CreateWaterRenderEntity(String debugName, RootEntity rootEntity, RelativeTransform localTransform)
  int Void CreateWaterRenderEntity_Impl(RenderId renderEntityId, String name, RenderId parentEntityId, RelativeTransform localTransform)
```

### `Keen.VRage.Render.Contracts.MaterialSystem`

```
  pub Void SetMaterialStates(ImmutableArray`1 materialStates)
  pub Void SetMaterials(ImmutableArray`1 materials)
  pub Void SetMaterialInstancingAssociation(MaterialInstancingAssociationConfiguration config)
  pub Void SetGlobalCustomData(TEntityData& globalData)
  int Void AddRuntimeMaterial(MaterialDefinition materialInstance)
  int Void ChangeMaterial(MaterialDefinition materialInstance)
  int Void ReuploadMaterial(MaterialDefinition materialInstance)
  int Void RemoveMaterial(MaterialDefinition materialInstance)
  pub Void SetMaterialOptions(MaterialStateOptionsDefinition materialStateOptions, Int32 value)
  int Void SetGlobalCustomDataBoxed_Impl(IGlobalGPUData globalData)
  int Void SetGlobalCustomData_Impl(CustomGPUDataPayload instanceData)
```

### `Keen.VRage.Render.Contracts.UISystem`

```
  pub Font GetFont(ResourceHandle`1 resourceHandle)
  pub Void PreloadTexture(ResourceHandle handle)
  pub Void SetMainViewportScale(Single scaleFactor)
  pub ImmediateDrawBatch CreateImmediateMainViewBatch(Int32 sortLayer, String debugName)
  pub PersistentDrawBatch CreatePersistentMainViewBatch(Int32 sortLayer, IDrawBatch previousBatch, Boolean deletePrevious)
  pub ImmediateDrawBatch CreateImmediateBatchFor(Nullable`1 renderTarget, Int32 sortLayer, String debugName)
  pub PersistentDrawBatch CreatePersistentBatchFor(Nullable`1 renderTarget, Int32 sortLayer, IDrawBatch previousBatch, Boolean deletePrevious)
  pub Vector2I GetTextureSize(ResourceHandle handle)
  int Void SubmitDrawBatch(RenderDrawCommandBuffer batch, RenderDrawCommandBuffer previousBatch, Int32 sortLayer, Boolean deletePrevious)
  int Void DisposeDrawBatchImmediate(RenderDrawCommandBuffer batch)
  int Void DisposeDrawBatchNextFrame(RenderDrawCommandBuffer batch)
```

## 5. Does DrawImage accept a render-target texture handle?

IL of the UI recorder's texture-handle classification. A generated
(render-target) handle must not fall into the file-backed content-cache
path — that throws inside the render thread's replay, which crashes.

### `VRage.Render: Keen.VRage.Render.Contracts.ImmediateDrawBatch.DrawImage`

```
  ldarg.0        
  call           Keen.VRage.Render.FrameData.RenderDrawCommandBuffer Keen.VRage.Render.Contracts.ImmediateDrawBatch::get_CommandBuffer()
  ldarg.1        
  ldarg.2        
  ldarg.3        
  ldarg.s        ignoreBounds
  ldarg.s        maskTexture
  ldarg.s        sourceRectangle
  callvirt       System.Void Keen.VRage.Render.FrameData.RenderDrawCommandBuffer::IDrawBatch_DrawImage(Keen.VRage.Library.Utils.ResourceHandle,Keen.VRage.Library.Mathe…
  ret            
```

### `VRage.Render: Keen.VRage.Render.Contracts.ImmediateDrawBatch.DrawImageExt`

```
  ldarg.0        
  call           Keen.VRage.Render.FrameData.RenderDrawCommandBuffer Keen.VRage.Render.Contracts.ImmediateDrawBatch::get_CommandBuffer()
  ldarg.1        
  ldarg.2        
  ldarg.3        
  ldarg.s        rotationPivot
  ldarg.s        rotation
  ldarg.s        ignoreBounds
  ldarg.s        rotationSpeed
  ldarg.s        maskTexture
  ldarg.s        sourceRectangle
  callvirt       System.Void Keen.VRage.Render.FrameData.RenderDrawCommandBuffer::IDrawBatch_DrawImageExt(Keen.VRage.Library.Utils.ResourceHandle,Keen.VRage.Library.Ma…
  ret            
```

### `VRage.Render: Keen.VRage.Render.Contracts.PersistentDrawBatch.DrawImage`

```
  ldarg.0        
  call           Keen.VRage.Render.FrameData.RenderDrawCommandBuffer Keen.VRage.Render.Contracts.PersistentDrawBatch::get_CommandBuffer()
  ldarg.1        
  ldarg.2        
  ldarg.3        
  ldarg.s        ignoreBounds
  ldarg.s        maskTexture
  ldarg.s        sourceRectangle
  callvirt       System.Void Keen.VRage.Render.FrameData.RenderDrawCommandBuffer::IDrawBatch_DrawImage(Keen.VRage.Library.Utils.ResourceHandle,Keen.VRage.Library.Mathe…
  ret            
```

### `VRage.Render: Keen.VRage.Render.Contracts.PersistentDrawBatch.DrawImageExt`

```
  ldarg.0        
  call           Keen.VRage.Render.FrameData.RenderDrawCommandBuffer Keen.VRage.Render.Contracts.PersistentDrawBatch::get_CommandBuffer()
  ldarg.1        
  ldarg.2        
  ldarg.3        
  ldarg.s        rotationPivot
  ldarg.s        rotation
  ldarg.s        ignoreBounds
  ldarg.s        rotationSpeed
  ldarg.s        maskTexture
  ldarg.s        sourceRectangle
  callvirt       System.Void Keen.VRage.Render.FrameData.RenderDrawCommandBuffer::IDrawBatch_DrawImageExt(Keen.VRage.Library.Utils.ResourceHandle,Keen.VRage.Library.Ma…
  ret            
```

### `VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.UISystemComponent.GetTexture`

```
  ldarg.1        
  call           System.Boolean Keen.VRage.Library.Extensions.ResourceHandleExtensions::IsGuid<Keen.VRage.Library.Utils.ResourceHandle>(!!0)
  ldstr          Unsupported resource handle type
  ldstr          texture.IsGuid()
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\SceneSystem\Components\UISystemComponent.cs
  ldc.i4         236
  call           System.Void Keen.VRage.Library.Diagnostics.Assert::True(System.Boolean,System.String,System.String,System.String,System.Int32)
  ldarg.0        
  ldfld          Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent Keen.VRage.Render12.SceneSystem.Components.UISystemComponent::_managedTe…
  ldarg.1        
  callvirt       Keen.VRage.Render12.Resources.ManagedResources.IManagedTexture Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent::GetTextu…
  ret            
```

### `VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.UISystemComponent.TryExtractGraphicsType`

```
  ldarg.0        
  call           !!1 Keen.VRage.Library.Extensions.ResourceHandleExtensions::GetMetadata<Keen.VRage.Library.Utils.ResourceHandle,Keen.VRage.Core.Utils.GraphicsMetadata…
  ldfld          Keen.VRage.Core.Utils.GraphicsMetadata/GraphicsTypeEnum Keen.VRage.Core.Utils.GraphicsMetadata::GraphicsType
  stloc.0        
  leave.s        IL_003e: ldloc.0
  isinst         System.Exception
  dup            
  brtrue.s       IL_001a: stloc.1
  pop            
  ldc.i4.0       
  br.s           IL_0032: endfilter
  stloc.1        
  ldloc.1        
  isinst         System.IO.InvalidDataException
  brtrue.s       IL_002e: ldc.i4.1
  ldloc.1        
  isinst         System.IO.IOException
  ldnull         
  cgt.un         
  br.s           IL_002f: ldc.i4.0
  ldc.i4.1       
  ldc.i4.0       
  cgt.un         
  endfilter      
  pop            
  ldarg.1        
  brfalse.s      IL_003a: leave.s IL_003c
  rethrow        
  leave.s        IL_003c: ldc.i4.0
  ldc.i4.0       
  ret            
  ldloc.0        
  ret            
```

### `VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder.DrawImage`

```
  ldarg.2        
  call           Keen.VRage.Library.Mathematics.Vector2 Keen.VRage.Library.Mathematics.BoundingBox2::get_Center()
  stloc.0        
  ldarg.0        
  ldarg.1        
  ldarg.2        
  ldarg.3        
  ldloc.0        
  ldc.r4         0
  ldarg.s        ignoreBounds
  ldc.r4         0
  ldarg.s        maskTexture
  ldarg.s        sourceRectangle
  call           System.Void Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder::DrawImageExt(Keen.VRage.Library.Utils.ResourceHandle,Keen.VR…
  ret            
```

### `VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder.DrawImageExt`

```
  ldsfld         Keen.VRage.Library.Mathematics.Vector2 Keen.VRage.Library.Mathematics.Vector2::UnitX
  stloc.0        
  ldarg.s        rotationSpeed
  ldc.r4         0
  beq.s          IL_002a: ldarg.s rotation
  ldarg.s        rotation
  ldarg.s        rotationSpeed
  ldsfld         Keen.VRage.Render12.Core.Systems.Time Keen.VRage.Render12.Core.CoreSystems::Time
  callvirt       System.TimeSpan Keen.VRage.Render12.Core.Systems.Time::get_FrameTime()
  stloc.2        
  ldloca.s       V_2
  call           System.Double System.TimeSpan::get_TotalSeconds()
  conv.r4        
  mul            
  add            
  starg.s        rotation
  ldarg.s        rotation
  ldc.r4         0
  beq.s          IL_0048: ldarg.1
  ldloca.s       V_0
  ldarg.s        rotation
  call           System.Single System.MathF::Cos(System.Single)
  ldarg.s        rotation
  call           System.Single System.MathF::Sin(System.Single)
  call           System.Void Keen.VRage.Library.Mathematics.Vector2::.ctor(System.Single,System.Single)
  ldarg.1        
  ldarg.0        
  ldfld          Keen.VRage.Render.CoreConfigurations.RenderConfiguration Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder::_renderConfig
  callvirt       System.Boolean Keen.VRage.Render.CoreConfigurations.RenderConfiguration::get_AnyResourceErrorFatal()
  call           Keen.VRage.Core.Utils.GraphicsMetadata/GraphicsTypeEnum Keen.VRage.Render12.SceneSystem.Components.UISystemComponent::TryExtractGraphicsType(Keen.VRag…
  stloc.1        
  ldloc.1        
  switch         Mono.Cecil.Cil.Instruction[]
  br             IL_013f: ldloca.s V_6
  ldarga.s       maskTexture
  call           System.Boolean System.Nullable`1<Keen.VRage.Library.Utils.ResourceHandle>::get_HasValue()
  brtrue.s       IL_007d: ldsfld Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent Keen.VRage.Render12.Core.CoreSystems::ManagedTextures
  ldnull         
  br.s           IL_008e: stloc.3
  ldsfld         Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent Keen.VRage.Render12.Core.CoreSystems::ManagedTextures
  ldarga.s       maskTexture
  call           !0 System.Nullable`1<Keen.VRage.Library.Utils.ResourceHandle>::get_Value()
  callvirt       Keen.VRage.Render12.Resources.ManagedResources.IManagedTexture Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent::GetTextu…
  stloc.3        
  ldsfld         Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent Keen.VRage.Render12.Core.CoreSystems::ManagedTextures
  ldarg.1        
  callvirt       Keen.VRage.Render12.Resources.ManagedResources.IManagedTexture Keen.VRage.Render12.Resources.ManagedResources.ManagedTextureManagerComponent::GetTextu…
  stloc.s        V_4
  ldarg.0        
  ldfld          Keen.VRage.Render12.UIStage.BatchBase.UIBatcher Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder::_uiBatcher
  ldloc.s        V_4
  ldarg.s        ignoreBounds
  ldc.i4.1       
  ldloc.3        
  callvirt       Keen.VRage.Render12.UIStage.Sprites.SpriteBatch Keen.VRage.Render12.UIStage.BatchBase.UIBatcher::GetSpriteBatch(Keen.VRage.Render12.Resources.ManagedR…
  ldarg.3        
  ldarg.s        rotationPivot
  ldloc.0        
  ldarg.s        sourceRectangle
  ldobj          System.Nullable`1<Keen.VRage.Library.Mathematics.BoundingBox2I>
  ldarg.2        
  ldobj          Keen.VRage.Library.Mathematics.BoundingBox2
  callvirt       System.Void Keen.VRage.Render12.UIStage.Sprites.SpriteBatch::Add(Keen.VRage.Library.Mathematics.ColorSRGB,Keen.VRage.Library.Mathematics.Vector2,Keen.…
  ret            
  ldarga.s       maskTexture
  call           System.Boolean System.Nullable`1<Keen.VRage.Library.Utils.ResourceHandle>::get_HasValue()
  ldc.i4.0       
  ceq            
  ldstr          Vector images does not support mask textures.
  ldstr          !maskTexture.HasValue
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\SceneSystem\Components\UISystemComponent.cs
  ldc.i4         597
  call           System.Void Keen.VRage.Library.Diagnostics.Assert::True(System.Boolean,System.String,System.String,System.String,System.Int32)
  ldarg.s        sourceRectangle
  call           System.Boolean System.Nullable`1<Keen.VRage.Library.Mathematics.BoundingBox2I>::get_HasValue()
  ldc.i4.0       
  ceq            
  ldstr          Vector images does not support source cutout. Use scissor rect instead.
  ldstr          !sourceRectangle.HasValue
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\SceneSystem\Components\UISystemComponent.cs
  ldc.i4         598
  call           System.Void Keen.VRage.Library.Diagnostics.Assert::True(System.Boolean,System.String,System.String,System.String,System.Int32)
  ldsfld         Keen.VRage.Render12.UIStage.Vectors.VectorImageManager Keen.VRage.Render12.Core.CoreSystems::VectorImages
  ldarg.1        
  callvirt       Keen.VRage.Render12.UIStage.Vectors.VectorImage Keen.VRage.Render12.UIStage.Vectors.VectorImageManager::GetVectorImage(Keen.VRage.Library.Utils.Resour…
  stloc.s        V_5
  ldloc.s        V_5
  callvirt       System.Boolean Keen.VRage.Render12.UIStage.Vectors.VectorImage::get_IsLoadedSuccessful()
  brfalse.s      IL_0176: ret
  ldarg.0        
  ldfld          Keen.VRage.Render12.UIStage.BatchBase.UIBatcher Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder::_uiBatcher
  ldloc.s        V_5
  ldarg.s        ignoreBounds
  callvirt       Keen.VRage.Render12.UIStage.Vectors.VectorImageBatch Keen.VRage.Render12.UIStage.BatchBase.UIBatcher::GetVectorImageBatch(Keen.VRage.Render12.UIStage.…
  ldarg.3        
  ldarg.s        rotationPivot
  ldloc.0        
  ldarg.2        
  ldobj          Keen.VRage.Library.Mathematics.BoundingBox2
  callvirt       System.Void Keen.VRage.Render12.UIStage.Vectors.VectorImageBatch::Add(Keen.VRage.Library.Mathematics.ColorSRGB,Keen.VRage.Library.Mathematics.Vector2,…
  ret            
  ldloca.s       V_6
  ldc.i4.s       32
  ldc.i4.1       
  call           System.Void System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::.ctor(System.Int32,System.Int32)
  ldloca.s       V_6
  ldstr          Graphics type 
  call           System.Void System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::AppendLiteral(System.String)
  ldloca.s       V_6
  ldloc.1        
  call           System.Void System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::AppendFormatted<Keen.VRage.Core.Utils.GraphicsMetadata/GraphicsTypeEnum>…
  ldloca.s       V_6
  ldstr           is not supported.
  call           System.Void System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::AppendLiteral(System.String)
  ldloca.s       V_6
  call           System.String System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::ToStringAndClear()
  newobj         System.Void System.NotSupportedException::.ctor(System.String)
  throw          
  ret            
```

## 6. View / viewport / pass / frame-graph types

Candidates for a second scene view. Names only — the shortlist gets a
full dump on the next pass once we know which are real.

```
-- Viewport: 0
-- RenderPass: 0
-- FrameGraph: 0
-- RenderView: 2
     VRage.Render12     Keen.VRage.Render12.Primitives.RenderView
     VRage.Render12     Keen.VRage.Render12.Primitives.RenderViewSlim
-- SceneView: 0
-- DrawScene: 0
-- RenderScene: 0
-- Mirror: 1
     VRage.Render12     Keen.VRage.Slug.Terathon.Slug.MirrorData
-- Reflection: 7
     VRage.DCS          Keen.VRage.DCS.Internal.JobReflectionIndexer
     VRage.Library      Keen.VRage.Library.Reflection.ReflectionExtensions
     VRage.Library      Keen.VRage.Library.Reflection.ReflectionInfoCache
     VRage.Library      Keen.VRage.Library.Reflection.ReflectionUtils
     VRage.Library      Keen.VRage.Library.Serialization.Binary.Reflection.ReflectionBinaryParser
     VRage.Library      Keen.VRage.Library.Utils.ReflectionBasedActivatorFactory
     VRage.Render12     Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection.ScreenSpaceReflections
-- Portal: 0
-- Preview: 30
     Game2.Client       CompiledAvaloniaXaml.!AvaloniaResources/NamespaceInfo:/UI/Shared/UGC/UGCPreviewItem.axaml
     Game2.Client       Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer.BlockPlacementPreview
     Game2.Client       Keen.Game2.Client.GameSystems.Render.PreviewHostComponent
     Game2.Client       Keen.Game2.Client.GameSystems.Render.PreviewHostDefinition
     Game2.Client       Keen.Game2.Client.GameSystems.Render.PreviewHostDefinitionObjectBuilder
     Game2.Client       Keen.Game2.Client.GameSystems.Render.PreviewHostDefinitionObjectBuilder_Migrations
     Game2.Client       Keen.Game2.Client.UI.Shared.UGC.Previews.BlueprintPreviewViewModel
     Game2.Client       Keen.Game2.Client.UI.Shared.UGC.Previews.ModPreviewViewModel
     Game2.Client       Keen.Game2.Client.UI.Shared.UGC.Previews.UGCPreviewViewModel
     Game2.Client       Keen.Game2.Client.UI.Shared.UGC.Previews.WorldPreviewViewModel
     Game2.Client       Keen.Game2.Client.UI.Shared.UGC.UGCPreviewItem
     Game2.Client       Keen.Game2.Client.WorldObjects.ArmorBlockPreviewRenderComponent
     Game2.Client       Keen.Game2.Client.WorldObjects.ArmorBlockPreviewRenderObjectBuilder
     Game2.Client       Keen.Game2.Client.WorldObjects.ArmorBlockPreviewRenderObjectBuilder_Migrations
     Game2.Client       Keen.Game2.Client.WorldObjects.CubeBlocks.Render.BlockPreviewComponent
     Game2.Client       Keen.Game2.Client.WorldObjects.CubeBlocks.Render.BlockPreviewObjectBuilder
     Game2.Client       Keen.Game2.Client.WorldObjects.CubeBlocks.Render.BlockPreviewObjectBuilder_Migrations
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewComponent
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewDefinition
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewDefinitionObjectBuilder
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewDefinitionObjectBuilder_Migrations
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewObjectBuilder
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewObjectBuilder_Migrations
     Game2.Client       Keen.Game2.Client.WorldObjects.Preview.SpherePreviewProcessorComponent
     VRage.Core         Keen.VRage.Core.Render.Materials.Templates.PaintPreviewMaterialDefinition
     VRage.Core         Keen.VRage.Core.Render.Materials.Templates.PaintPreviewMaterialDefinitionObjectBuilder
     VRage.Core         Keen.VRage.Core.Render.Materials.Templates.PaintPreviewMaterialDefinitionObjectBuilder_Migrations
     VRage.DCS          Keen.VRage.DCS.Annotations.HideInPreviewAttribute
     VRage.Render       Keen.VRage.Render.Data.GPUDataConvertor.PaintPreviewMaterialCompositor
     VRage.Render       Keen.VRage.Render.Data.PaintPreviewMeshData
```

### Methods named like a scene/view render entry point

```
VRage.Render     Keen.VRage.Render.SessionComponents.PerformanceStatsRecorderSessionComponent.RecordRenderFrame(FrameStatistics& frameStats)
VRage.Render     Keen.VRage.Render.SessionComponents.PerformanceStatsRecorderSessionComponent.Keen.VRage.Render.Utils.IVRageRenderStatsRecorder.RecordRenderFrame(FrameStatistics& modreq(System.Runtime.InteropServices.InAttribute) frameStats)
VRage.Render     Keen.VRage.Render.FrameData.SharedData.GetRenderFrame(Boolean& isPreFrame, Boolean onlyFullFrame)
VRage.Render     Keen.VRage.Render.FrameData.UpdateData.GetRenderFrame(Boolean& isPreFrame, Boolean onlyFullFrame)
VRage.Render     Keen.VRage.Render.EngineComponents.StatsRecorderEngineComponent.ForwardRenderFrame(FrameStatistics& frameStats)
VRage.Render     Keen.VRage.Render.Contracts.RenderFrameTime.PreRenderFrame(TimeSpan newFrameTime, TimeSpan newAnimationFrameTime)
VRage.Render12   Keen.VRage.Render12.Primitives.Frame.CameraSettings.EnableCompositeRenderView()
VRage.Render12   Keen.VRage.Render12.PostProcessStage.Water.SurfelGenerationJob.BuildCullingRenderView(BoundingBox aabb, MatrixD& viewD, MatrixD& invViewD)
VRage.Render12   Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.GetSunRenderView()
VRage.Render12   Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.GetCrossSectionRenderView(Vector3D center, Vector3D forward, Vector3D up, Single nearPlaneHalfSize)
VRage.Render12   Keen.VRage.Render12.EngineComponents.Render12EngineComponent.get_RenderFrameStatistics()
VRage.Render12   Keen.VRage.Render12.EngineComponents.Render12EngineComponent.get_RenderFrameTime()
VRage.Render12   Keen.VRage.Render12.EngineComponents.Render12EngineComponent.RenderFrameGuard()
VRage.Render12   Keen.VRage.Render12.EngineComponents.Render12EngineComponent.RenderFrame()
VRage.Render12   Keen.VRage.Render12.EngineComponents.Render12EngineComponent.<RenderFrame>g__DebugSleep|67_0(RenderSettings settings)
VRage.Render12   Keen.VRage.Render12.Core.Systems.SceneDrawSystem.DrawRenderViewFrustum(RenderView& view)
VRage.Render12   Keen.VRage.Render12.Core.Systems.SettingsManager.get_FreezedRenderView()
VRage.Render12   Keen.VRage.Render12.Core.Systems.SettingsManager.get_PreviousRenderView()
VRage.Render12   Keen.VRage.Render12.Core.Systems.SettingsManager.get_RenderView()
VRage.Render12   Keen.VRage.Render12.Core.Contracts.ContractsProcessor.ProcessRenderFrame(UpdateFrame frame, Boolean draw, Boolean flipUIOnCompleted, Boolean returnShared)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration.get_RenderCamera()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration.set_RenderCamera(PrefabDefinition value)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration.get_RenderCameraDefinition()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder.RenderCameraAccessor()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder.RenderCameraDefinitionAccessor()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_0()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_1(ClientPlayersSessionConfiguration instance)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_2(ClientPlayersSessionConfiguration instance, PrefabDefinition& value)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder/<>c.<RenderCameraDefinitionAccessor>b__6_0()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfiguration/TypeInfoHolder/<>c.<RenderCameraDefinitionAccessor>b__6_1(ClientPlayersSessionConfiguration instance)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfigurationObjectBuilder/TypeInfoHolder.RenderCameraAccessor()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfigurationObjectBuilder/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_0()
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfigurationObjectBuilder/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_1(ClientPlayersSessionConfigurationObjectBuilder instance)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfigurationObjectBuilder/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_2(ClientPlayersSessionConfigurationObjectBuilder instance, PrefabDefinition& value)
Game2.Client     Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionConfigurationObjectBuilder/TypeInfoHolder/<>c.<RenderCameraAccessor>b__5_3(ClientPlayersSessionConfigurationObjectBuilder instance)
Game2.Client     Keen.Game2.Client.GameSystems.CameraSystems.CameraControllerChildComponent.UpdateRenderCamera(DEntity camera, EntityData`1 worldTransformsWritable, WorldTransform worldTransform)
Game2.Client     Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent.get_RenderCameraEntity()
Game2.Client     Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent.set_RenderCameraEntity(Entity value)
```

## 7. RenderView plumbing

### `Keen.VRage.Render12.Primitives.RenderView` (fields)

```
  pub RenderView Default
  int ProjectionMatrices <Projection>k__BackingField
  int ProjectionMatrices <JitteredProjection>k__BackingField
  int Vector2 <JitterPixelOffset>k__BackingField
  int Int32 <JitterPhaseCount>k__BackingField
  int Int32 <JitterCounter>k__BackingField
  int MatrixD <InvViewD>k__BackingField
  int Single <NearClipping>k__BackingField
  int Single <FarClipping>k__BackingField
  int Single <VeryFarClipping>k__BackingField
  int Vector2 <ProjectionOffset>k__BackingField
  int Boolean <LastUpdateWasSmooth>k__BackingField
  int Nullable`1 <LocalEnvironmentRoot>k__BackingField
  int Nullable`1 <LocalEnvironmentRootBB>k__BackingField
  int Nullable`1 <LocalShadowsStreamingRoot>k__BackingField
  int Vector3 <LocalShadowsStreamingOffset>k__BackingField
  int Vector3D <CameraPosition>k__BackingField
  int Single <FovV>k__BackingField
  int Matrix <InvViewAt0>k__BackingField
  int Single <LargeDistanceFarClipping>k__BackingField
  int Matrix <ViewAt0>k__BackingField
  int MatrixD <ViewD>k__BackingField
  int Boolean <IsOrthographic>k__BackingField
  int Boolean <IsFirstPerson>k__BackingField
  int Double <MaxBufferedCameraSpeed>k__BackingField
  int Vector3D <LastFrameCameraPosition>k__BackingField
  int Vector3D <LastFrameCameraPositionWithCuts>k__BackingField
  int Queue`1 _cameraSpeedBuffer
  int Vector2I _resolution
  int Single _defaultFovH
  int Single _fovH
```

### `Keen.VRage.Render12.Primitives.RenderViewSlim` (fields)

```
  pub MatrixD InvViewD
  pub MatrixD ViewD
  pub Matrix Projection
  pub Single CullingFarPlane
```

### Members typed RenderView / RenderViewSlim

```
prop   Keen.VRage.Render12.SceneSystem.Components.ILocalShadowComponent.PrimaryView setter=none
prop   Keen.VRage.Render12.SceneSystem.Components.ILocalShadowComponent.ShadowMaskCullingView setter=none
prop   Keen.VRage.Render12.SceneSystem.Components.LightEntityComponent.PrimaryView setter=none
prop   Keen.VRage.Render12.SceneSystem.Components.LightEntityComponent.ShadowMaskCullingView setter=none
field  Keen.VRage.Render12.SceneSystem.Components.LightEntityComponent/ShadowMaskCullingViewSlim.View pub
field  Keen.VRage.Render12.Primitives.RenderView.Default pub static
field  Keen.VRage.Render12.LightingStage.CascadeUpdateInfo.RenderViewSlim pub
field  Keen.VRage.Render12.LightingStage.EnvironmentProbeManager/Render.View pub
field  Keen.VRage.Render12.LightingStage.LocalLightsManager/DepthUpdateRequest.View pub
field  Keen.VRage.Render12.LightingStage.LocalLightsManager/ShadowMaskUpdateRequest.CullingView pub
field  Keen.VRage.Render12.LightingStage.LocalLightsManager/ShadowMaskUpdateRequest.BaseRenderView pub
field  Keen.VRage.Render12.Core.Systems.SettingsManager._renderView int
field  Keen.VRage.Render12.Core.Systems.SettingsManager._previousRenderView int
field  Keen.VRage.Render12.Core.Systems.SettingsManager._freezedRenderView int
prop   Keen.VRage.Render12.Core.Systems.SettingsManager.FreezedRenderView setter=none
prop   Keen.VRage.Render12.Core.Systems.SettingsManager.PreviousRenderView setter=none
prop   Keen.VRage.Render12.Core.Systems.SettingsManager.RenderView setter=none
```

## 8. Frame structure

### `Keen.VRage.Render12.EngineComponents.Render12EngineComponent.RenderFrame` — call sequence

```
  call Render12EngineComponent.get_Conc
  call RenderThreadManager.AssertRenderThread
  call Render12EngineComponent.get_Conc
  call Time.get_FrameTime
  call Time.get_AnimationFrameTime
  call RenderFrameTime.PreRenderFrame
  call Profiler.Begin
  call Profiler.Begin
  call Render12EngineComponent.get_RT
  call IPlatformWindow.NextFrame
  brtrue.s
  call ProfilingScope.Dispose
  call Render12EngineComponent.get_RT
  brfalse
  call Render12EngineComponent.get_RT
  brfalse
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call IExternalLog.LogToExternalDebugger
  call Singleton`1.get_Instance
  call ThreadPool.Suspend
  call Singleton`1.get_Instance
  call TimeSpan.FromMilliseconds
  call ThreadPool.WaitForAllTasks
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call IExternalLog.LogToExternalDebugger
  call IPlatformRender.SuspendRenderContext
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call IExternalLog.LogToExternalDebugger
  br
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call IPlatformRender.ResumeRenderContext
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call Singleton`1.get_Instance
  call ThreadPool.Resume
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call Render12EngineComponent.get_RT
  call Render12EngineComponent.get_RT
  brfalse.s
  call Log.get_Default
  brtrue.s
  br.s
  call Log.WriteLine
  call Log.get_Default
  brtrue.s
  br.s
  call Log.Flush
  call Render12EngineComponent.get_RT
  brfalse.s
  call LoadingTimeTracker.Begin
  br
  call Profiler.Begin
  call Profiler.Begin
  call Render12EngineComponent.get_RT
  call Render12EngineComponent.IRender_Present
  call Nullable`1..ctor
  call ProfilingScope.Dispose
  call Stopwatch.GetTimestamp
  call TimeSpan..ctor
  call Render12EngineComponent.get_RT
  call TimeSpan.get_Ticks
  ble.s
  call Render12EngineComponent.get_RT
  call TimeSpan.op_Subtraction
  call Render12EngineComponent.get_RT
  call Stopwatch.GetTimestamp
  call TimeSpan..ctor
  call Render12EngineComponent.get_RT
  call TimeSpan.op_Subtraction
  call ProfilingScope.Dispose
  call Profiler.Begin
  call Profiler.Begin
  call Stopwatch.GetTimestamp
  call TimeSpan..ctor
  call Render12EngineComponent.get_Conc
  call TimeSpan.get_Ticks
  ble.s
  call TimeSpan.op_Subtraction
  call Render12EngineComponent.get_Conc
  call ClientToRenderMinimizer.UpdateClientToRenderShiftTime
  call TimeSpan.get_TotalSeconds
  call Render12EngineComponent.ProcessMessages
  call TimeSpan.op_Addition
  call TimeSpan.op_Addition
  call Render12EngineComponent.get_Conc
  call RenderContracts.GetSettings
  call RenderSettings.get_InternalStateSettings
  brtrue.s
  call ScreenBuffers.GetCurrentFrameRenderTarget
  br.s
  call ScreenBuffers.get_FinalLDRTexture
  call Render12EngineComponent.get_RT
  call IPlatformWindow.get_DrawEnabled
  brfalse.s
  call Render12EngineComponent.get_Conc
  call RenderContracts.GetSettings
  call RenderSettings.get_InternalStateSettings
  brfalse.s
  call Render12EngineComponent.get_Conc
  call RenderContracts.GetSettings
  call RenderSettings.get_InternalStateSettings
  brfalse.s
  call Profiler.Begin
  call Render12EngineComponent.Draw
  call ProfilingScope.Dispose
  call Profiler.Begin
  call Render12EngineComponent.Draw
  call ProfilingScope.Dispose
  call ProfilingScope.Dispose
  call Profiler.Begin
  call ContractsProcessor.get_SharedData
  call SharedData.AfterRender
  call ProfilingScope.Dispose
  call ProfilingScope.Dispose
  call Render12EngineComponent.get_Conc
  call RenderContracts.GetSettings
  call Render12EngineComponent.<RenderFrame>g__DebugSleep|67_0
  call Stopwatch.GetTimestamp
  call TimeSpan..ctor
  call Render12EngineComponent.get_RT
  call TimeSpan.op_Subtraction
  call TimeSpan.op_Subtraction
  call Render12EngineComponent.get_RT
  call TimeSpan.op_Addition
  call VideoMemoryMonitor.get_AvailableVRAM
  call VideoMemoryMonitor.get_UsedVRAM
  call Render12EngineComponent.get_Conc
  call RenderFrameStatistics.Enqueue
  call TimeSpan.op_Addition
  call Render12EngineComponent.SetTimings
  call Render12EngineComponent.get_RT
  brfalse.s
  call LoadingTimeTracker.End
  call Render12EngineComponent.get_RT
  call Render12EngineComponent.get_Conc
  call Interlocked.Increment
  call ProfilingScope.Dispose
```

### `Keen.VRage.Render12.Core.Contracts.ContractsProcessor.ProcessRenderFrame` — call sequence

```
  call Profiler.Begin
  call SettingsManager.get_System
  brfalse.s
  call RenderCommandBuffer.get_IsWaiting
  brtrue.s
  call Assert.True
  call Assert.True
  brfalse.s
  brtrue.s
  call ContractsProcessor.ReplayCommandBuffer
  br.s
  brfalse.s
  brfalse.s
  call ContractsProcessor.ReplayCommandBuffer
  br.s
  brtrue.s
  brtrue.s
  call ContractsProcessor.ReplayCommandBuffer
  brtrue.s
  brfalse.s
  call SceneManager.get_SceneSystems
  call Entity.Get
  call UISystemComponent.ProcessEnqueuedUIChanges
  call ContractsProcessor.ClearFrame
  call ProfilingScope.Dispose
```

### `Keen.VRage.Render12.Primitives.Frame.CameraSettings.EnableCompositeRenderView` — call sequence

```
```

## 9. MainRenderTarget

```
  pub Task TakeScreenshotAsync(FileHandleWritable saveFile, Nullable`1 downsampleResolution, Nullable`1 viewport, Boolean withoutUi, Boolean awaitWrite)
  int Void TakeScreenshot_Impl(FileHandleWritable saveFile, TaskCompletionSource taskCompletionSource, Nullable`1 downsampleResolution, Nullable`1 viewport, Boolean withoutUi, Boolean awaitWrite)
  pub Void .ctor()
```

## 10. Composite render view

```
Keen.VRage.Render.Options.RenderOptions/CloudPreset..cctor  field  CompositeFilterRadius
Keen.VRage.Render.Options.RenderOptions/CloudPreset..cctor  field  CompositeFilterRadius
Keen.VRage.Render.Options.RenderOptions/CloudPreset..cctor  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings.Convert  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings.Convert  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings..ctor  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings..ctor  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings..cctor  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/GPUImprint.PrintMembers  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/GPUImprint.GetHashCode  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/GPUImprint.Equals  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/GPUImprint.Equals  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.TryDeserializeMember  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.SerializeMembers  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.TryDeserializeMember  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.DeserializeFast  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.SerializeMembers  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.TryDeserializeMember  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/Serializer.CollectFields  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/TypeInfoHolder/<>c.<CompositeFilterRadiusAccessor>b__27_0  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/TypeInfoHolder/<>c.<CompositeFilterRadiusAccessor>b__27_1  field  CompositeFilterRadius
Keen.VRage.Render.Data.CloudSettings/TypeInfoHolder/<>c.<CompositeFilterRadiusAccessor>b__27_2  field  CompositeFilterRadius
Keen.VRage.Render.Data.RenderSettingsManipulator.ApplyRenderOptions  field  CompositeFilterRadius
Keen.VRage.Render.Data.RenderSettingsManipulator.ApplyRenderOptions  field  CompositeFilterRadius
Keen.VRage.Render12.Resources.Shaders.ShaderHandles..cctor  field  CloudComposite
Keen.VRage.Render12.Resources.Shaders.ShaderHandles..cctor  field  HologramComposite
Keen.VRage.Render12.PostProcessStage.CloudJob/JobSnapshot/<InitializeAsync>d__8.MoveNext  field  CloudComposite
Keen.VRage.Render12.GeometryStage.Passes.HologramPassJob/<InitializeAsync>d__9.MoveNext  field  HologramComposite
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderLocalLightShadows  ->  CameraSettings.EnableCompositeRenderView
Keen.VRage.Library.Mathematics.FixedPointSerializer..ctor  field  <0>__Migration_CompositeToDecimal
Keen.VRage.Library.Mathematics.FixedPointSerializer..ctor  field  <0>__Migration_CompositeToDecimal
Keen.VRage.Library.Localization.LocStringSerializer..ctor  field  <0>__Migration_CompositeToString
Keen.VRage.Library.Localization.LocStringSerializer..ctor  field  <0>__Migration_CompositeToString
Keen.VRage.DCS.Serialization.EOBUpdateContext.Migrate  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext.MarkCompositeAsLost  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext..ctor  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext..ctor  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext/RejectEntitiesWithLostComposites.ValidateAsync  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext/RejectEntitiesWithLostComposites.ValidateAsync  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext/TypeInfoHolder/<>c.<LostCompositesAccessor>b__5_0  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext/TypeInfoHolder/<>c.<LostCompositesAccessor>b__5_1  field  LostComposites
Keen.VRage.DCS.Serialization.EOBUpdateContext/TypeInfoHolder/<>c.<LostCompositesAccessor>b__5_2  field  LostComposites
Keen.VRage.DCS.Definitions.EntityCompositeDefinition.Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinition>.get_Cloner  field  <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinition>.Cloner>k__BackingField
Keen.VRage.DCS.Definitions.EntityCompositeDefinition..cctor  field  <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinition>.Cloner>k__BackingField
Keen.VRage.DCS.Definitions.EntityCompositeDefinition/IBuildStrategy.CompileDependencyOrder  field  localComposite
Keen.VRage.DCS.Definitions.EntityCompositeDefinition/IBuildStrategy/<>c__DisplayClass1_0.<CompileDependencyOrder>b__0  field  localComposite
Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder.Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder>.get_Cloner  field  <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder>.Cloner>k__BackingField
Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder..cctor  field  <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder>.Cloner>k__BackingField
Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder/ComponentInfo.Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder.ComponentInfo>.get_Cloner  field  <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder.ComponentInfo>.Cloner>k__BackingField
Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder/ComponentInfo..cctor  field  <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder.ComponentInfo>.Cloner>k__BackingField
Keen.Game2.Client.UI.TerminalScreen.ControlPanel.ControlPanelViewModel/<EditTile>d__118.MoveNext  field  Composite
Keen.Game2.Client.UI.TerminalScreen.ControlPanel.ControlPanelViewModel/<EditTile>d__118.MoveNext  field  Composite
Keen.Game2.Client.UI.TerminalScreen.ControlPanel.ControlPanelViewModel/<EditTile>d__119.MoveNext  field  Composite
Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelEntityModel/TypeInfoHolder/<>c.<CompositeAccessor>b__6_1  field  Composite
Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelEntityModel/TypeInfoHolder/<>c.<CompositeAccessor>b__6_2  field  Composite
Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelEntityModel/TypeInfoHolder/<>c.<CompositeAccessor>b__6_3  field  Composite
Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelEntityModelServer..ctor  field  Composite
Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.StreamedControlPanelSessionComponent.<GetAvailableActions>g__CollectComposites|29_0  field  Composite
Keen.Game2.Simulation.WorldObjects.CubeBlocks.Tools.BlockToolsComponent.OnBlockAdded  field  _toolCompositeCache
Keen.Game2.Simulation.WorldObjects.CubeBlocks.Tools.BlockToolsComponent.OnBlockAdded  field  _toolCompositeCache
Keen.Game2.Simulation.WorldObjects.CubeBlocks.Tools.BlockToolsComponent..ctor  field  _toolCompositeCache
Keen.Game2.Simulation.GameSystems.Missions.MissionPostprocessor.get_BlockComposites  field  _blockComposites
Keen.Game2.Simulation.GameSystems.Missions.MissionPostprocessor.PostProcess  field  _blockComposites
Keen.Game2.Simulation.GameSystems.Missions.MissionPostprocessor..ctor  field  _blockComposites
```

### `CameraSettings` members

```
  field Single _defaultFovTan
  field GPUWorldTransform ViewTransform
  field GPUWorldTransform InvViewTransform
  field Matrix ViewAt0
  field Matrix InvViewAt0
  field Matrix Projection
  field Matrix InvProjection
  field Vector4 MainViewCameraPos
  field Matrix LocalViewToMainViewClip
  field Vector4 PositionDelta
  field Single CameraSpeed
  field Single DetailLevel
  field Single FOVScaleFactor
  field Int32 CameraFlags
  field Single TanFOV
  field Vector3 _padding
  CameraSettings CreateJitteredCameraSettings(RenderView&)
  CameraSettings CreateNonjitteredCameraSettings(RenderView&)
  CameraSettings CreateCameraSettings(RenderView&, Boolean)
  TrackedCameraSettings op_Explicit(CameraSettings&)
  Void EnableCompositeRenderView()
```

## 11. Scene draw stages

Types in the Render12 stage namespaces, with any method that takes a
view or a target — the signature that a second pass would need.

```
Keen.VRage.Render12.UIStage.MainUISystem
    DoWork(DirectCommandList commandList, IRenderTargetView rt, Vector2I viewport)
Keen.VRage.Render12.UIStage.OffscreenUIRenderer
    DrawOne(DirectCommandList commandList, UISystemComponent uiSystem, OffscreenRenderTargetComponent target)
Keen.VRage.Render12.TransparentStage.ParticleRenderingJob
    DoWork(DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers, ParticleContext context, Vector2I resolution, IRenderTargetView accumBuffer, IRenderTargetView coverageBuffer, IDepthStencilView depthBuffer, ITexture2DView exposureBuffer, IRenderTargetView motionVectors, Nullable`1 fsrMasks, IStructuredBufferView emitters, IStructuredBufferView emitterInstances)
Keen.VRage.Render12.TransparentStage.ResolveOITJob
    DoWork(DirectCommandList commandList, ResizableRWRenderTargetTexture renderTarget, Nullable`1 fsrMasks, ITexture2DView accumBuffer, ITexture2DView coverageBuffer)
Keen.VRage.Render12.TransparentStage.ResolveStochasticTransparencyJob
    DoWork(DirectCommandList commandList, IRenderTargetView backgroundTarget, ITexture2DView exposureTexture, StochasticTransparencyContext context)
    ApplyBackgroundAttenuation(DirectCommandList commandList, IRenderTargetView backgroundTarget, StochasticTransparencyContext context)
    DoWaterSurfaceResolve(DirectCommandList commandList, IRenderTargetView backgroundTarget, ITexture2DView exposureTexture, StochasticTransparencyContext context)
    ComposeBackgroundWithVisibility(DirectCommandList commandList, IRenderTargetView backgroundTarget, StochasticTransparencyContext context, ITexture2DView source, ITexture2DView surfaceVisibility)
    MotionFirefliesSuppression(DirectCommandList commandList, ITexture2DView inputMotionTarget, IRenderTargetView outputMotionTarget)
    DenoiseMotion(DirectCommandList commandList, ITexture2DView inputMotionTarget, IRenderTargetView outputMotionTarget, Int32 stride)
    DenoiseRadiance(DirectCommandList commandList, ITexture2DView inputRadianceTarget, ITexture2DView backgroundVisibility, ITexture2DView exposureTexture, IRenderTargetView outputRadianceTarget, Int32 stride)
    RadianceFirefliesSuppression(DirectCommandList commandList, ITexture2DView inputRadianceTarget, IRenderTargetView outputRadianceTarget)
    ComposeBackground(DirectCommandList commandList, IRenderTargetView backgroundTarget, StochasticTransparencyContext context)
    ComposeBackground(DirectCommandList commandList, IRenderTargetView backgroundTarget, StochasticTransparencyContext context, ITexture2DView source)
Keen.VRage.Render12.PrepareStage.CullingEntityProxyJob
    DoWork(ComputeCommandList commandList, EntityProxyContext targetContext, OutputGeometryBufferContext outputGeometryBuffers, VisibilityListBufferContext visibilityListBufferContext, RenderViewSlim viewSlim, OcclusionContext occlusionContext, Boolean isFirstPass, Nullable`1& posViewToNegViewProj, Nullable`1 baseRenderView, Int32 rootEntityId, Boolean show3DMap, CharacterCullingBehavior characterCullingBehavior, Int32 cascadeIndex)
Keen.VRage.Render12.PrepareStage.CullingGeometryJob
    DoWork(ComputeCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, VisibilityListBufferContext visibilityListBufferContext, RenderViewSlim viewSlim, PassLODSettings passLODSetting, Nullable`1& posViewToNegViewProj, Boolean wasViewMoveSmooth, LODTransitionContext lodTransitions, OcclusionContext occlusionContext, Boolean isFirstPass, Nullable`1 baseRenderView, Int32 rootEntityId, Boolean hideUI, Boolean show3DMap, CharacterCullingBehavior characterCullingBehavior)
Keen.VRage.Render12.PrepareStage.GrassRendering
    DoWork(DirectCommandList commandList, GrassBufferContext grassBufferContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView[] renderTargets, ResizableDepthStencilTexture depthTarget, ResizableRWRenderTargetTexture hizBuffer, GrassSettings grassSettings, EntityProxyContext culledProxies)
    GenerateGrassInstances(DirectCommandList commandList, ComputePSO instanceDataGenerationPSO, TransientConstantBuffer grassConstantBuffer, RWBuffer generationCommands, ResizableRWBuffer generationData, GrassBufferContext grassBufferContext, OutputGeometryBufferContext outputGeometryBuffers, ResizableRWRenderTargetTexture hizBuffer, EntityProxyContext culledProxies, TransientConstantBuffer entityProxyContextSetupBuffer)
Keen.VRage.Render12.PostProcessStage.CloudJob
    DoWork(DirectCommandList commandList, ResizableDepthStencilTexture depthTexture, IRenderTargetView lBuffer, IRenderTargetView vBuffer, IRenderTargetView oitAccumBuffer, IRenderTargetView oitCoverageBuffer, IRenderTargetView motionVectors, Nullable`1 fsrMasks)
Keen.VRage.Render12.PostProcessStage.CopyJob
    DoWork(DirectCommandList commandList, IRenderTargetView destination, ITexture2DView source, Nullable`1 viewport, Nullable`1 postProcess, Channel channelFlags, ITexture2DView opacitySource, Nullable`1 cropRect)
Keen.VRage.Render12.PostProcessStage.DebugHistogramJob
    DoWork(DirectCommandList commandList, IRenderTargetView rtView, ITexture2DView debugHistogram)
Keen.VRage.Render12.PostProcessStage.DisplayHDRIntensity
    DoWork(DirectCommandList commandList, IRenderTargetView destination, ITexture2DView source, Nullable`1 viewport)
Keen.VRage.Render12.PostProcessStage.EnvironmentProbeBlending
    DoWork_BlendWeight(DirectCommandList commandList, Int32 faceIndex, Single blendWeight, RenderTargetCubeTexture inputTexture, RenderTargetCubeTexture outputTexture)
    DoWork_BlendFactor(DirectCommandList commandList, Single blendFactor, RenderTargetCubeTexture inputTextureA, RenderTargetCubeTexture inputTextureB, RenderTargetCubeTexture outputTexture)
Keen.VRage.Render12.PostProcessStage.FlaresOcclusionJob
    DoWork(DirectCommandList commandList, FlaresContext context, EntityProxyContext entityProxyContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView rtView, ITexture2DView coverageBuffer, ITexture2DView visibilityBuffer)
Keen.VRage.Render12.PostProcessStage.FlaresRenderingJob
    DoWork(DirectCommandList commandList, FlaresContext context, IRenderTargetView rtView)
Keen.VRage.Render12.PostProcessStage.FXAAJob
    DoWork(DirectCommandList commandList, IRenderTargetView destination, ITexture2DView source)
Keen.VRage.Render12.PostProcessStage.HBAOJob
    DoWork(DirectCommandList commandList, HBAOSettings& settings, IRenderTargetView rtView, ITexture2DView depthTexture, ITexture2DView normalTexture)
Keen.VRage.Render12.PostProcessStage.HighlightJob
    DoWork(DirectCommandList commandList, CullingContext& cullingContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView rtView, ITexture2DView exposure, ResizableDepthStencilTexture occluderDepthBuffer)
Keen.VRage.Render12.PostProcessStage.LocalFogJob
    DoWork(DirectCommandList commandList, IRenderTargetView colorOutput, IRenderTargetView visibility, ITexture2DView depth, ITexture2DView cloudDepth, ITexture2DView cloudAlpha)
Keen.VRage.Render12.PostProcessStage.MipMapJobExtensions
    DoWork(MipMapJob job, ComputeCommandList commandList, RWRenderTargetTexture target, Int32 mipsCount)
    DoWork(MipMapJob job, ComputeCommandList commandList, ResizableRWRenderTargetTexture target, Int32 mipsCount)
    MipGetter(RWRenderTargetTexture target, Int32 level)
    MipGetter(ResizableRWRenderTargetTexture target, Int32 level)
Keen.VRage.Render12.PostProcessStage.Water.WaterContext
    ResizeToScreen(CopyCommandList command, ResizableRWRenderTargetTexture target)
    ResizeToSSEffects(CopyCommandList command, ResizableRWRenderTargetTexture target)
    ResizeToDownscaledWater(CopyCommandList command, ResizableRWRenderTargetTexture target)
    ResizeAndClear(DirectCommandList commandList, ResizableRWRenderTargetTexture target, Func`3 resize)
Keen.VRage.Render12.PostProcessStage.Water.WaterJob
    DoWaterRender(RawWaterBuffers rawBuffers, PostProcessedWaterBuffers postProcessedBuffers, DirectCommandList commandList, ResizableRWRenderTargetTexture renderTarget, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, OutputGeometryBufferContext outputGeometryBuffers, Nullable`1 fsrMasks, IRenderTargetView accumulationBuffer, IRenderTargetView coverageBuffer, ITexture2DView exposure)
    DoWaterLine(DirectCommandList commandList, ResizableRWRenderTargetTexture renderTarget, ITexture2DView insideMask, Vector2I insideMaskResolution)
    DoWaterSurface(DirectCommandList commandList, ResizableRWRenderTargetTexture renderTarget, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, OutputGeometryBufferContext outputGeometryBuffers, Nullable`1 fsrMasks, IRenderTargetView accumulationBuffer, IRenderTargetView coverageBuffer)
Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob
    DrawGrids(DirectCommandList commandList, JobSnapshot snapshot, WaterMeshContext context, RawWaterBuffers output, RWRenderTargetTexture insideMask, TransientConstantBuffer globalWaterSettings, TransientConstantBuffer stillSurfaceConfig, ITexture2DArrayView stillHeight, ITexture2DArrayView stillNormals, ITexture2DView flowHeight, ITexture2DView flowNormals)
    ComputeInsideMask(DirectCommandList commandList, JobSnapshot snapshot, WaterMeshContext context, RWRenderTargetTexture insideMask, WaterDepthLayers crossSectionUD, WaterDepthLayers crossSectionLR)
Keen.VRage.Render12.PostProcessStage.Water.WaterShadingJob
    DoWork(DirectCommandList commandList, IRenderTargetView outputColor, ITexture2DView waterDepthTexture, ITexture2DView waterThicknessTexture, ITexture2DView velocityTexture, ITexture2DView profilesTexture, ITexture2DView normalsTexture, ITexture2DArrayView stillHeightmaps, ITexture2DArrayView stillNormals, ITexture2DView flowHeightmap, ITexture2DView flowNormal, ITexture2DView srcBackground, TransientConstantBuffer waterConfig, TransientConstantBuffer stillSurfaceConfig, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView reactiveMask, IRenderTargetView transparencyCompositionMask, IRenderTargetView accumulationBuffer, IRenderTargetView coverageBuffer, ITexture2DView samplePosition1, ITexture2DView samplePosition2, ITexture2DView surfaceHeightNormal, ITexture2DView foamTexture, WaterDepthLayers sunDepthLayers, ITexture2DView surfaceAlphaTexture, Boolean surfaceOnly)
    DoWaterLine(DirectCommandList commandList, IRenderTargetView outputColor, ITexture2DView insideMask, WaterLineSettings& waterLineSettings, Vector2I screenResolution, Vector2I insideMaskResolution)
Keen.VRage.Render12.PostProcessStage.Water.WaterEffects.ScreenSpaceAreaOfEffect
    Advection(DirectCommandList commandList, JobSnapshot snapshot, ScreenSpaceEffectsContext context, TransientConstantBuffer ssWaterCommonInputs, TransientConstantBuffer surfaceConfigs, ITexture2DArrayView stillWaterHeight, ITexture2DArrayView stillWaterNormals, ITexture2DView flowWaterHeight, ITexture2DView flowWaterNormal, ITexture2DView waterDepth, ITexture2DView waterNormals, ResizableRWRenderTargetTexture waterVelocity, ITexture2DView waterThickness, IDepthStencilView depthStencil)
    DoWork(DirectCommandList commandList, ScreenSpaceEffectsContext context, ITexture2DView waterDepth, ITexture2DView waterNormals, ResizableRWRenderTargetTexture waterVelocity, ITexture2DView waterThickness, TransientConstantBuffer ssWaterCommonInputs, TransientConstantBuffer surfaceConfigs, ITexture2DArrayView stillWaterHeight, ITexture2DArrayView stillWaterNormals, ITexture2DView flowWaterHeight, ITexture2DView flowWaterNormal, IDepthStencilView depthStencil, Matrix& highFovProjection)
Keen.VRage.Render12.PostProcessStage.Upsampling.FSR3.FSR3
    PingPong(Boolean isOddFrame, RWRenderTargetTexture even, RWRenderTargetTexture odd)
Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection.ScreenSpaceReflections
    DoWork(DirectCommandList commandList, TransientConstantBuffer configCBuffer, ResizableRWRenderTargetTexture destination, ResizableDepthStencilTexture depthBuffer, ResizableRWRenderTargetTexture roughnessGBuffer, ResizableRWRenderTargetTexture normalGBuffer, ResizableRWRenderTargetTexture motionVectors)
Keen.VRage.Render12.LightingStage.AmbientLightJob
    DoWork(DirectCommandList commandList, IRenderTargetView rtView, ITexture2DView giBufferDiffuse, ITexture2DView giBufferSpecular)
Keen.VRage.Render12.LightingStage.AtmosphereAdditiveJob
    DoWork(DirectCommandList commandList, IRenderTargetView rtView)
    DrawAtmospheres(Int32 envDataIndex, JobSnapshot snapshot, DirectCommandList commandList, IRenderTargetView output, IRenderTargetView blurOutput)
Keen.VRage.Render12.LightingStage.AtmosphereLUTJob
    DoWork(DirectCommandList commandList, IRenderTargetView LUTTarget, AtmosphereConstants& atmosphereConstants)
Keen.VRage.Render12.LightingStage.AtmosphereMultiplyJob
    DoWork(DirectCommandList commandList, IRenderTargetView rtView)
Keen.VRage.Render12.LightingStage.CascadeUpdateInfo
    .ctor(DepthStencilTexture depthTexture, RenderViewSlim renderViewSlim, Nullable`1 posViewToNegViewProj, CameraSettings renderCameraSettings, Int32 cascadeIndex)
Keen.VRage.Render12.LightingStage.CubeTextureMipMapGenerationJob
    DoWork(DirectCommandList commandList, Int32 faceIndex, RenderTargetCubeTexture finalTexture, RenderTargetCubeTexture transferTexture)
Keen.VRage.Render12.LightingStage.DirectionalLightJob
    DoWork(DirectCommandList commandList, ITexture2DView shadowRtView, DirectionalLightShadowResources shadowResources, IRenderTargetView rtView)
Keen.VRage.Render12.LightingStage.DirectionalLightShadowJob
    DoWork(DirectCommandList commandList, DirectionalLightShadowResources shadowResources, ResizableRenderTargetTexture rtView)
Keen.VRage.Render12.LightingStage.IndirectPlanetEnvironmentJob
    DoWork(DirectCommandList commandList, TransientConstantBuffer cameraSettingsBuffer, IRenderTargetView environmentProbeCloseTarget, IRenderTargetView environmentProbeFarTarget, ITexture2DView environmentProbeDepthTexture, RenderViewSlim& view)
Keen.VRage.Render12.LightingStage.IRCacheDebugJob
    DoWork(DirectCommandList commandList, IRenderTargetView lBuffer)
Keen.VRage.Render12.LightingStage.LocalLightsJob
    DoWork(DirectCommandList commandList, IRenderTargetView rtView, IRenderTargetView rtViewDiffuseOnly, ClusteringContext clusteringResult, OutputGeometryBufferContext outputGeometryBuffers)
Keen.VRage.Render12.LightingStage.MipMapPreFilterJob
    DoWork(DirectCommandList commandList, RenderTargetCubeTexture sourceTexture, RenderTargetCubeTexture targetTexture, Int32 faceIndex, SampleQuality sampleCount)
    DoWork(DirectCommandList commandList, IManagedCubeTexture sourceTexture, RenderTargetCubeTexture targetTexture, Int32 faceIndex, SampleQuality sampleCount)
    PrefilterMipMaps(ScreenQuadJob job, DirectCommandList commandList, RenderTargetCubeTexture targetCubeTexture, Constants constants, Int32 faceIndex)
Keen.VRage.Render12.LightingStage.RaytraceGIJob
    DispatchSpatialStep(DirectCommandList commandList, BorrowedResourcesReSTIR borrowed, RTGIContext context, ResizableRWRenderTargetTexture input, ResizableRWRenderTargetTexture output, Int32 stepIndex, Vector2I gridSize)
Keen.VRage.Render12.LightingStage.SkyboxMotionVectorsJob
    DoWork(DirectCommandList commandList, IRenderTargetView rtView)
Keen.VRage.Render12.GeometryStage.Passes.HologramPassJob
    DoWork(DirectCommandList commandList, GeometryContext geometryContextFirstPass, OutputGeometryBufferContext geometryBuffers, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, IRenderTargetView oitAccumBuffer, IRenderTargetView oitCoverageBuffer, T rtView, IRenderTargetView motionVectors, ITexture2DView exposure, Nullable`1 fsrMasks, GeometryContext geometryContextSecondPass)
Keen.VRage.Render12.GeometryStage.Passes.IndirectEnvironmentPassJob
    DoWork(DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers, TransientConstantBuffer cameraSettingsBuffer, RenderViewSlim& view, GeometryContext result, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, IRenderTargetView rt, IDepthStencilView depthStencil, Boolean clearRenderTarget)
Keen.VRage.Render12.GeometryStage.Passes.SurfelPassJob
    DoWork(DirectCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView dummyRT, Vector2I resolution, TransientConstantBuffer cameraSettings, SurfelBuffer surfelBuffer, TransientConstantBuffer surfelSetup)
Keen.VRage.Render12.GeometryStage.Passes.TopMostPassJob
    DoWork(DirectCommandList commandList, GeometryContext& geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView rtView)
Keen.VRage.Render12.GeometryStage.Passes.TransparentPassJob
    DoWork(DirectCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, IRenderTargetView oitAccumBuffer, IRenderTargetView oitCoverageBuffer, T rtView, IRenderTargetView motionVectors, ITexture2DView exposure, Nullable`1 fsrMasks, ITexture2DView depthHierarchy, Nullable`1 sssrBuffer, ITexture2DView waterDepthBuffer, ITexture2DView waterThicknessBuffer, IDepthStencilView depthBuffer, ITexture2DView occluderDepthBuffer)
Keen.VRage.Render12.GeometryStage.Passes.UnlitPassJob
    DoWork(DirectCommandList commandList, GeometryContext& geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView rtView, ITexture2DView exposure)
Keen.VRage.Render12.DebugStage.DebugPassJob
    ConsumeDebugOutput(DirectCommandList commandList, IRenderTargetView destination)
    DrawCubeMap(DirectCommandList commandList, IRenderTargetView destinationTexture, ROCubeTexture sourceCubeTexture, Boolean normalize)
    DrawCubeMap(DirectCommandList commandList, IRenderTargetView destinationTexture, RenderTargetCubeTexture sourceCubeTexture, Int32 targetMip, Boolean displayIntensity)
    DrawCubeMap(DirectCommandList commandList, IRenderTargetView destinationTexture, DepthStencilCubeTexture sourceCubeTexture, Boolean normalize)
Keen.VRage.Render12.DebugStage.StencilDebugJob
    DoJob(DirectCommandList commandList, IRenderTargetView rtv, ITexture2DView stencilView, Int32 stencilMask)
Keen.VRage.Render12.Core.Systems.SceneDrawSystem
    Draw(ResizableRWRenderTargetTexture finalLDRBuffer)
    ExecuteForwardAndPostProcess(ResizableRWRenderTargetTexture lBuffer, Nullable`1& screenshotTexture, ResizableRWRenderTargetTexture finalLDRBuffer)
    ExecuteForwardPasses(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
    ExecutePostPasses(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer, ResizableRWRenderTargetTexture lBuffer, Nullable`1& screenshotTexture, Boolean saveScreenshotWithoutUi)
    ComputeExposure(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, ITexture2DView& exposure, Nullable`1& debugHistogram)
    PatchHoles(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
    ProcessPreUpscaleDebugView(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
    UpscaleTargetFSR(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer, ResizableRWRenderTargetTexture lBuffer, ITexture2DView exposure, Nullable`1& tempLDRBuffer, Nullable`1& tempHDRBuffer, ResizableRWRenderTargetTexture& toneMappingInput, ResizableRWRenderTargetTexture& toneMappingOutput)
    ApplyBloom(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingInput, ITexture2DView exposure, Borrowed`1& bloom)
    ApplyToneMapping(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingInput, ResizableRWRenderTargetTexture toneMappingOutput, ITexture2DView exposure, ResizableRenderTargetTexture bloom)
    ProcessPostUpscaleDebugView(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingOutput, ResizableRWRenderTargetTexture lBuffer, ResizableRWRenderTargetTexture finalLDRBuffer)
    ApplyNonFSRUpscalingAndAA(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingOutput, ITexture2DView exposure, ResizableRWRenderTargetTexture finalLDRBuffer)
Keen.VRage.Render12.Core.Systems.ScreenBuffers
    set_GBuffer(ResizableRWRenderTargetTexture[] value)
    set_FinalLDRTexture(ResizableRWRenderTargetTexture value)
    set_FinalLDRPlaceholder(ResizableRWRenderTargetTexture value)
```

## 12. Environment probes — the only arbitrary-viewpoint scene render

### `Keen.VRage.Render12.LightingStage.EnvironmentProbeManager`

```
  pub field Int32 MAX_STATE_COUNT
  int field Single NEAR_PLANE
  int field Single MAX_BLEND_WEIGHT
  int field Single <LastLocalLightAmbient>k__BackingField
  int field RenderTargetCubeTexture _closeFinalTexture
  int field RenderTargetCubeTexture _closeBlendTexture
  int field RenderTargetCubeTexture _closeWorkTextureA
  int field RenderTargetCubeTexture _closeWorkTextureB
  int field RenderTargetCubeTexture _farFinalTexture
  int field RenderTargetCubeTexture _farBlendTexture
  int field RenderTargetCubeTexture _farWorkTextureA
  int field RenderTargetCubeTexture _farWorkTextureB
  int field EnvironmentProbeSettings _lastSettings
  int field Boolean _forceReprocess
  int field Int32 _state
  int field TimeSpan _startedUpdateTime
  prop ITextureCubeView CloseIBL
  prop ITextureCubeView FarIBL
  prop RenderTargetCubeTexture CloseEnvProbe
  prop RenderTargetCubeTexture FarEnvProbe
  prop Single LastLocalLightAmbient
  pub Void Dispose()
  pub Buffer`1 PrepareProbes()
  int Single GetBlendWeight()
  int Void UpdateLocalLightAmbient()
  int Void RecreateProbes(EnvironmentProbeSettings& settings)
  int Boolean NeedsReprocess(EnvironmentProbeSettings& settings)
  pub Void OnResetContext()
  int Void DisposeTextures()
  int Single GatherLightAmbient(Vector3D cameraPosition)
  pub Void .ctor()
```

### `EnvironmentProbeComponent` — not found

### `EnvironmentProbeEntity` — not found

### Probe references from outside VRage.Render12

```
VRage.Render: Keen.VRage.Render.Data.EnvironmentSettings/EnvironmentProbeSettings.Equals -> EnvironmentProbeSettings.Equals
VRage.Render: Keen.VRage.Render.Data.EnvironmentSettings/EnvironmentProbeSettings.DeepClone -> EnvironmentProbeSettings.DeepClone
VRage.Render: Keen.VRage.Render.Data.EnvironmentSettings/EnvironmentProbeSettings/Serializer..ctor -> EnvironmentProbeSettings_Migrations..ctor
VRage.Render: Keen.VRage.Render.Data.EnvironmentSettings/EnvironmentProbeSettings/Cloner.Keen.VRage.Library.Utils.Cloning.IDeepCloner<Keen.VRage.Render.Data.EnvironmentSettings.EnvironmentProbeSettings>.Clone -> EnvironmentProbeSettings..ctor
```

## 13. Hologram pass

```
VRage.Render: Keen.VRage.Render.Data.HologramSettings
    Convert(IGPUDataConvertor)
    GetStreamSerializer(SerializerFormat)
    .ctor(HologramSettings&, CloningContext&)
    DeepClone()
    DeepClone(CloningContext&)
    GetTypeInfo()
    .cctor()
    <Convert>g__GetGPUHologramMaterials|36_0(HologramMaterialSettings)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/HologramMaterialSettings
    GetStreamSerializer(SerializerFormat)
    .ctor(HologramMaterialSettings&, CloningContext&)
    DeepClone()
    DeepClone(CloningContext&)
    GetTypeInfo()
    .cctor()
VRage.Render: Keen.VRage.Render.Data.HologramSettings/HologramMaterialSettings/Serializer
    DeserializeInto(JsonDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(JsonDeserializationContext&, HologramMaterialSettings&, SerializerFlags)
    DeserializeMembers(JsonDeserializationContext&, HologramMaterialSettings&, SerializerFlags)
    TryDeserializeMember(JsonDeserializationContext&, Int32, ReadOnlySpan`1&, HologramMaterialSettings&, SerializerFlags)
    Deserialize(JsonDeserializationContext&, SerializerFlags)
    Serialize(JsonSerializationContext&, HologramMaterialSettings&, SerializerFlags)
    Serialize(JsonSerializationContext&, Object, SerializerFlags)
    SerializeMembers(JsonSerializationContext&, HologramMaterialSettings&, SerializerFlags)
    SerializeMembers(JsonSerializationContext&, Object, SerializerFlags)
    DeserializeMembers(JsonDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(BinaryDeserializationContext&, Object&, SerializerFlags)
    DeserializeIntoSlow(BinaryDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(BinaryDeserializationContext&, HologramMaterialSettings&, SerializerFlags)
    DeserializeMembers(BinaryDeserializationContext&, HologramMaterialSettings&, SerializerFlags)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/HologramMaterialSettings/Cloner
    Keen.VRage.Library.Utils.Cloning.IDeepCloner<Keen.VRage.Render.Data.HologramSettings.HologramMaterialSettings>.Clone(HologramMaterialSettings, CloningContext&)
    .ctor()
VRage.Render: Keen.VRage.Render.Data.HologramSettings/HologramMaterialSettings/TypeInfoHolder
    .cctor()
    OpacityOverrideAccessor()
    OpacityPulseAmountAccessor()
    AmbientIntensityAccessor()
    SpecularFactorAccessor()
    ProjectorNoiseMinAccessor()
    ProjectorNoiseMaxAccessor()
    ProjectorScanlineMinAccessor()
    ProjectorScanlineMaxAccessor()
    ProjectorScanlinePowerAccessor()
    ProjectorPulseFrequencyAccessor()
    ProjectorRimlightColorAccessor()
    ProjectorRimlightPowerAccessor()
    ProjectorRimlightPulseAmountAccessor()
VRage.Render: Keen.VRage.Render.Data.HologramSettings/HologramMaterialSettings/TypeInfoHolder/<>c
    .cctor()
    .ctor()
    <OpacityOverrideAccessor>b__4_0(HologramMaterialSettings&)
    <OpacityOverrideAccessor>b__4_1(HologramMaterialSettings&, Single&)
    <OpacityPulseAmountAccessor>b__5_0(HologramMaterialSettings&)
    <OpacityPulseAmountAccessor>b__5_1(HologramMaterialSettings&, Single&)
    <AmbientIntensityAccessor>b__6_0(HologramMaterialSettings&)
    <AmbientIntensityAccessor>b__6_1(HologramMaterialSettings&, Single&)
    <SpecularFactorAccessor>b__7_0(HologramMaterialSettings&)
    <SpecularFactorAccessor>b__7_1(HologramMaterialSettings&, Single&)
    <ProjectorNoiseMinAccessor>b__8_0(HologramMaterialSettings&)
    <ProjectorNoiseMinAccessor>b__8_1(HologramMaterialSettings&, Single&)
    <ProjectorNoiseMinAccessor>b__8_2(HologramMaterialSettings&)
    <ProjectorNoiseMaxAccessor>b__9_0(HologramMaterialSettings&)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/GPUHologramMaterialSettings
    ToString()
    PrintMembers(StringBuilder)
    op_Inequality(GPUHologramMaterialSettings, GPUHologramMaterialSettings)
    op_Equality(GPUHologramMaterialSettings, GPUHologramMaterialSettings)
    GetHashCode()
    Equals(Object)
    Equals(GPUHologramMaterialSettings)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/GPUImprint
    ToString()
    PrintMembers(StringBuilder)
    op_Inequality(GPUImprint, GPUImprint)
    op_Equality(GPUImprint, GPUImprint)
    GetHashCode()
    Equals(Object)
    Equals(GPUImprint)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/Serializer
    DeserializeInto(JsonDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(JsonDeserializationContext&, HologramSettings&, SerializerFlags)
    DeserializeMembers(JsonDeserializationContext&, HologramSettings&, SerializerFlags)
    TryDeserializeMember(JsonDeserializationContext&, Int32, ReadOnlySpan`1&, HologramSettings&, SerializerFlags)
    Deserialize(JsonDeserializationContext&, SerializerFlags)
    Serialize(JsonSerializationContext&, HologramSettings&, SerializerFlags)
    Serialize(JsonSerializationContext&, Object, SerializerFlags)
    SerializeMembers(JsonSerializationContext&, HologramSettings&, SerializerFlags)
    SerializeMembers(JsonSerializationContext&, Object, SerializerFlags)
    DeserializeMembers(JsonDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(BinaryDeserializationContext&, Object&, SerializerFlags)
    DeserializeIntoSlow(BinaryDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(BinaryDeserializationContext&, HologramSettings&, SerializerFlags)
    DeserializeMembers(BinaryDeserializationContext&, HologramSettings&, SerializerFlags)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/HologramMaterialSettings_Migrations
    .ctor()
    .cctor()
    GetMigrations()
    GetLayoutDeltas()
    GetSchema(Version)
VRage.Render: Keen.VRage.Render.Data.HologramSettings/Cloner
    Keen.VRage.Library.Utils.Cloning.IDeepCloner<Keen.VRage.Render.Data.HologramSettings>.Clone(HologramSettings, CloningContext&)
    .ctor()
VRage.Render: Keen.VRage.Render.Data.HologramSettings/TypeInfoHolder
    .cctor()
    ProjectorDefaultAccessor()
    ProjectorSelectedAccessor()
    ProjectorBlockedAccessor()
    PlacementPreviewAccessor()
    ForcedHologramMaterialAccessor()
    EmissivityMultiplierAccessor()
    EmissivityMinAccessor()
    IncludeCharacterLightAccessor()
    WriteMotionVectorsAccessor()
    WriteDepthAccessor()
    ProjectorLinesScreenSpaceAccessor()
    ChromaticOffsetsAccessor()
    DefaultChromaticOffsetAccessor()
VRage.Render: Keen.VRage.Render.Data.HologramSettings/TypeInfoHolder/<>c
    .cctor()
    .ctor()
    <ProjectorDefaultAccessor>b__4_0()
    <ProjectorDefaultAccessor>b__4_1(HologramSettings&)
    <ProjectorDefaultAccessor>b__4_2(HologramSettings&, HologramMaterialSettings&)
    <ProjectorDefaultAccessor>b__4_3(HologramSettings&)
    <ProjectorSelectedAccessor>b__5_0()
    <ProjectorSelectedAccessor>b__5_1(HologramSettings&)
    <ProjectorSelectedAccessor>b__5_2(HologramSettings&, HologramMaterialSettings&)
    <ProjectorSelectedAccessor>b__5_3(HologramSettings&)
    <ProjectorBlockedAccessor>b__6_0()
    <ProjectorBlockedAccessor>b__6_1(HologramSettings&)
    <ProjectorBlockedAccessor>b__6_2(HologramSettings&, HologramMaterialSettings&)
    <ProjectorBlockedAccessor>b__6_3(HologramSettings&)
VRage.Render: Keen.VRage.Render.Data.HologramState
VRage.Render: Keen.VRage.Render.Data.HologramStateExtensions
    GetStateEncodedAsOpacity(HologramState)
VRage.Render: Keen.VRage.Render.Data.HologramSettings_Migrations
    .ctor()
    .cctor()
    GetMigrations()
    GetLayoutDeltas()
    GetSchema(Version)
    Migrate_0_12_21_800(Object&)
    Migrate_2_0_1_5060(Object&)
VRage.Render: Keen.VRage.Render.Data.HologramSettings_Migrations/<>O
VRage.Render: Keen.VRage.Render.Data.HologramSettings_Migrations/<>o__15
VRage.Render: Keen.VRage.Render.Data.HologramSettings_Migrations/<>o__16
VRage.Render: Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject
    GetStreamSerializer(SerializerFormat)
    .ctor()
    .ctor(HologramDebugObject&, CloningContext&)
    DeepClone()
    DeepClone(CloningContext&)
    GetTypeInfo()
    .cctor()
VRage.Render: Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject/Serializer
    DeserializeInto(JsonDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(JsonDeserializationContext&, HologramDebugObject&, SerializerFlags)
    DeserializeMembers(JsonDeserializationContext&, HologramDebugObject&, SerializerFlags)
    TryDeserializeMember(JsonDeserializationContext&, Int32, ReadOnlySpan`1&, HologramDebugObject, SerializerFlags)
    Deserialize(JsonDeserializationContext&, SerializerFlags)
    Serialize(JsonSerializationContext&, HologramDebugObject&, SerializerFlags)
    Serialize(JsonSerializationContext&, Object, SerializerFlags)
    SerializeMembers(JsonSerializationContext&, HologramDebugObject&, SerializerFlags)
    SerializeMembers(JsonSerializationContext&, Object, SerializerFlags)
    DeserializeMembers(JsonDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(BinaryDeserializationContext&, Object&, SerializerFlags)
    DeserializeIntoSlow(BinaryDeserializationContext&, Object&, SerializerFlags)
    DeserializeInto(BinaryDeserializationContext&, HologramDebugObject&, SerializerFlags)
    DeserializeMembers(BinaryDeserializationContext&, HologramDebugObject&, SerializerFlags)
VRage.Render: Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject/Cloner
    Keen.VRage.Library.Utils.Cloning.IDeepCloner<Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject>.Clone(HologramDebugObject, CloningContext&)
    .ctor()
VRage.Render: Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject/TypeInfoHolder
    .cctor()
    HologramSettingsAccessor()
    .ctor()
VRage.Render: Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject/TypeInfoHolder/<>c
    .cctor()
    .ctor()
    <HologramSettingsAccessor>b__4_0()
    <HologramSettingsAccessor>b__4_1(HologramDebugObject)
    <HologramSettingsAccessor>b__4_2(HologramDebugObject, HologramSettings&)
VRage.Render: Keen.VRage.Render.Data.RenderDebugObjects.HologramDebugObject_Migrations
    .ctor()
    .cctor()
    GetMigrations()
    GetLayoutDeltas()
    GetSchema(Version)
VRage.Render12: Keen.VRage.Render12.GeometryStage.Passes.HologramPassJob
    .ctor(List`1)
    InitializeAsync()
    Dispose()
    CreateResources()
    CopyDepthLate(DirectCommandList)
    DoWork(DirectCommandList, GeometryContext, OutputGeometryBufferContext, ClusteringContext, DirectionalLightShadowResources, IRenderTargetView, IRenderTargetView, T, IRenderTargetView, ITexture2DView, Nullable`1, GeometryContext)
    IsHologramPassUsed(ReadOnlySpan`1)
    <DoWork>g__DoForwardPass|13_0(GeometryContext, <>c__DisplayClass13_0`1&)
VRage.Render12: Keen.VRage.Render12.GeometryStage.Passes.HologramPassJob/<>c__DisplayClass13_0`1
VRage.Render12: Keen.VRage.Render12.GeometryStage.Passes.HologramPassJob/<InitializeAsync>d__9
    MoveNext()
    SetStateMachine(IAsyncStateMachine)
```

## 14. How an LCD surface texture reaches its material

### `LcdPanelSurfaceContext.SetNewScreenMaterialHandle`

```
  call  LcdPanelSurfaceContext.ReleaseScreenMaterialHandle
  call  LcdContentRendererSessionComponent.CreateRuntimeLcdMaterial
  call  Nullable`1..ctor
  fld   LcdPanelSurfaceContext._screenMaterialHandle
```

### `LcdPanelSurfaceRenderComponent.TransitionToCustomRender`

```
  fld   LcdPanelSurfaceRenderComponent._surfaces
  fld   LcdPanelSurfaceRenderComponent._lcdBlock
  call  LcdMultiPanelComponent.GetSurfaceState
  fld   LcdPanelSurfaceContext.CurrentMaterialState
  fld   LcdPanelSurfaceContext.ContentDirty
  call  LcdPanelSurfaceRenderComponent.RebuildSurfaceContent
  call  LcdPanelSurfaceRenderComponent.ReturnRenderTarget
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.Resolution
  fld   LcdPanelSurfaceState.Orientation
  call  LcdPanelSurfaceRenderComponent.IsOrientationSwapped
  fld   Vector2I.Y
  fld   Vector2I.X
  call  Vector2I..ctor
  call  DefaultInterpolatedStringHandler..ctor
  call  DefaultInterpolatedStringHandler.AppendLiteral
  call  Component.get_Entity
  call  Entity.get_DebugName
  call  DefaultInterpolatedStringHandler.AppendFormatted
  call  DefaultInterpolatedStringHandler.AppendLiteral
  call  Component.get_Entity
  call  Entity.get_DEntity
  call  DefaultInterpolatedStringHandler.AppendFormatted
  call  DefaultInterpolatedStringHandler.AppendLiteral
  call  DefaultInterpolatedStringHandler.AppendFormatted
  call  DefaultInterpolatedStringHandler.ToStringAndClear
  fld   LcdPanelSurfaceRenderComponent._rtPool
  call  LcdRenderTargetPoolSessionComponent.Borrow
  call  Nullable`1..ctor
  fld   LcdPanelSurfaceContext.RenderTarget
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.Resolution
  fld   Vector2I.X
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.Resolution
  fld   Vector2I.Y
  fld   LcdPanelSurfaceRenderComponent._renderer
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.DefaultScreenMaterial
  fld   LcdPanelSurfaceState.Orientation
  fld   LcdPanelSurfaceContext.RenderTarget
  call  Nullable`1.get_Value
  call  OffscreenRenderTarget.get_TextureHandle
  call  Nullable`1..ctor
  call  LcdPanelSurfaceContext.SetNewScreenMaterialHandle
  fld   LcdPanelSurfaceContext.CurrentMaterialState
  call  LcdPanelSurfaceRenderComponent.UpdateMaterialReplacements
  call  LcdPanelSurfaceRenderComponent.RebuildSurfaceContent
```

### `LcdPanelSurfaceRenderComponent.UpdateMaterialReplacements`

```
  fld   LcdPanelSurfaceRenderComponent._surfaces
  fld   LcdPanelSurfaceRenderComponent._surfaces
  fld   LcdPanelSurfaceRenderComponent._render
  call  BlockRenderComponent.SetMaterialReplacements
  call  LcdPanelSurfaceRenderComponent.ApplyHiddenParts
  fld   LcdPanelSurfaceRenderComponent._surfaces
  call  List`1..ctor
  call  HashSet`1..ctor
  fld   LcdPanelSurfaceRenderComponent._surfaces
  fld   LcdPanelSurfaceContext.CurrentMaterialState
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.UseOnlineTexture
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.MeshPartName
  call  HashSet`1.Add
  fld   LcdPanelSurfaceContext.CurrentMaterialState
  call  LcdPanelSurfaceContext.get_ScreenMaterial
  fld   LcdPanelSurfaceRenderComponent._lcdBlock
  call  LcdMultiPanelComponent.get_Definition
  call  LcdMultiPanelDefinition.get_PowerOffMaterial
  call  LcdPanelSurfaceContext.get_ScreenMaterial
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.DefaultScreenMaterial
  call  LcdPanelSurfaceContext.get_ScreenMaterial
  fld   LcdPanelSurfaceRenderComponent._lcdBlock
  call  LcdMultiPanelComponent.get_Definition
  call  LcdMultiPanelDefinition.get_PowerOffMaterial
  call  LcdPanelSurfaceContext.get_Definition
  fld   LcdPanelSurface.MeshPartName
  call  MeshPartMaterialPair..ctor
  call  List`1.Add
  fld   LcdPanelSurfaceRenderComponent._surfaces
  fld   LcdPanelSurfaceRenderComponent._modelComponent
  fld   BlockModelComponent.Definition
  call  ModelDefinition.get_Model
  call  Dictionary`2..ctor
  call  ImmutableArray.ToImmutableArray
  call  Dictionary`2.set_Item
  fld   LcdPanelSurfaceRenderComponent._render
  call  DictionaryReader`2.op_Implicit
  call  Nullable`1..ctor
  call  BlockRenderComponent.SetMaterialReplacements
  call  LcdPanelSurfaceRenderComponent.ApplyHiddenParts
```

## 15. Handle types for the blit

### `Keen.VRage.Library.Utils.ResourceHandle`

```
  int field UInt128 _key
  int field IDeepCloner`1 <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Library.Utils.ResourceHandle>.Cloner>k__BackingField
  pub ctor(GeneratedResourceHandle& handle)
  pub ctor(Guid& guid)
  int ctor(ResourceHandle& instance, CloningContext& context)
  int ctor()
  prop UInt128 Keen.VRage.Library.Utils.IResourceHandle.Key
  prop Type Keen.VRage.Library.Utils.IResourceHandle.ResourceType
  prop IDeepCloner`1 Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Library.Utils.ResourceHandle>.Cloner
  pub op_Implicit : ResourceHandle <- (GeneratedResourceHandle&)
  pub op_Equality : Boolean <- (ResourceHandle, ResourceHandle)
  pub op_Inequality : Boolean <- (ResourceHandle, ResourceHandle)
  pub Boolean Equals(ResourceHandle other)
  pub Boolean Equals(Object obj)
  pub Int32 GetHashCode()
  pub String ToString()
  pub ResourceHandle GetOrRegister(FileHandle file, Boolean logWarningOnRegister)
  int ResourceHandle GetOrRegisterInternal(FileHandle file, String projectIdentifier, Boolean logWarningOnRegister)
  pub ResourceHandle NewGuidResourceHandle()
  pub Guid NewGuid()
  int Void ValidateHandle(FileHandle file)
  pub ResourceHandle DeepClone()
  pub ResourceHandle DeepClone(CloningContext& context)
```

### `Keen.VRage.Library.Utils.ResourceHandle`1`

```
  int field UInt128 _key
  int field IDeepCloner`1 <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Library.Utils.ResourceHandle<T>>.Cloner>k__BackingField
  pub ctor(Guid& guid)
  pub ctor(GeneratedResourceHandle& handle)
  int ctor(ResourceHandle`1& instance, CloningContext& context)
  int ctor()
  prop UInt128 Keen.VRage.Library.Utils.IResourceHandle.Key
  prop Type Keen.VRage.Library.Utils.IResourceHandle.ResourceType
  prop IDeepCloner`1 Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Library.Utils.ResourceHandle<T>>.Cloner
  pub op_Equality : Boolean <- (ResourceHandle`1, ResourceHandle`1)
  pub op_Inequality : Boolean <- (ResourceHandle`1, ResourceHandle`1)
  pub op_Implicit : ResourceHandle <- (ResourceHandle`1&)
  pub op_Explicit : ResourceHandle`1 <- (ResourceHandle&)
  pub Boolean Equals(ResourceHandle`1 other)
  pub Boolean Equals(Object obj)
  pub Int32 GetHashCode()
  pub String ToString()
  pub ResourceHandle`1 DeepClone()
  pub ResourceHandle`1 DeepClone(CloningContext& context)
  int ResourceHandle Keen.VRage.Library.Utils.IResourceHandle<Keen.VRage.Library.Utils.ResourceHandle<T>>.op_Implicit(ResourceHandle`1& modreq(System.Runtime.InteropServices.InAttribute) handle)
```

### `Keen.VRage.Library.Utils.GeneratedResourceHandle`

```
  int field RenderId _id
  pub ctor(RenderId id)
  pub op_Equality : Boolean <- (GeneratedResourceHandle, GeneratedResourceHandle)
  pub op_Inequality : Boolean <- (GeneratedResourceHandle, GeneratedResourceHandle)
  pub Boolean Equals(GeneratedResourceHandle other)
  pub Boolean Equals(Object obj)
  pub Int32 GetHashCode()
  pub String ToString()
```

### `Keen.VRage.Render.Contracts.OffscreenRenderTarget`

```
  int field RenderId <Id>k__BackingField
  prop RenderId Id
  prop Boolean IsValid
  prop ResourceHandle`1 TextureHandle
  pub Void Dispose()
  pub Void TakeScreenshotToMemory(Boolean waitUntilFullyLoaded)
```

### `Keen.VRage.Library.Utils.RenderId`

```
  int field Int64 _data
  prop Boolean IsEmpty
  prop Boolean IsValid
  pub Boolean Equals(RenderId other)
  pub Boolean Equals(Object obj)
  pub Int32 GetHashCode()
  pub String ToString()
```

### `Keen.VRage.Library.Mathematics.BoundingBox2`

```
  pub field Vector2 Min
  pub field Vector2 Max
  int field IDeepCloner`1 <Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Library.Mathematics.BoundingBox2>.Cloner>k__BackingField
  pub ctor(Vector2 min, Vector2 max)
  int ctor(BoundingBox2& instance, CloningContext& context)
  int ctor()
  prop Vector2 Keen.VRage.Library.Mathematics.Generics.IBoundingBox2<Keen.VRage.Library.Mathematics.BoundingBox2,Keen.VRage.Library.Mathematics.Vector2,System.Single>.Min
  prop Vector2 Keen.VRage.Library.Mathematics.Generics.IBoundingBox2<Keen.VRage.Library.Mathematics.BoundingBox2,Keen.VRage.Library.Mathematics.Vector2,System.Single>.Max
  prop Vector2 Center
  prop Vector2 HalfExtents
  prop Vector2 Extents
  prop Single Width
  prop Single Height
  prop Vector2 Size
  prop ISerializer`1 Keen.VRage.Library.Serialization.ISerializable<Keen.VRage.Library.Mathematics.BoundingBox2>.Serializer
  prop IDeepCloner`1 Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Library.Mathematics.BoundingBox2>.Cloner
  pub op_Equality : Boolean <- (BoundingBox2, BoundingBox2)
  pub op_Inequality : Boolean <- (BoundingBox2, BoundingBox2)
  pub op_Explicit : BoundingBox2 <- (BoundingBox2D&)
  pub op_Explicit : BoundingBox2 <- (BoundingBox2I)
  int BoundingBox2 Keen.VRage.Library.Mathematics.Generics.IBoundingBox2<Keen.VRage.Library.Mathematics.BoundingBox2,Keen.VRage.Library.Mathematics.Vector2,System.Single>.Create(Vector2 min, Vector2 max)
  pub BoundingBox2 CreateMerged(BoundingBox2 original, BoundingBox2 additional)
  pub BoundingBox2 CreateFromPoints(IEnumerable`1 points)
  pub BoundingBox2 CreateFromHalfExtent(Vector2 center, Single halfExtent)
  pub BoundingBox2 CreateFromHalfExtent(Vector2 center, Vector2 halfExtent)
  pub BoundingBox2 CreateInvalid()
  pub Boolean Equals(BoundingBox2 other)
  pub Boolean Equals(Object obj)
  pub Int32 GetHashCode()
  pub String ToString()
  pub BoundingBox2 Intersect(BoundingBox2 box)
  pub Boolean Intersect(BoundingBox2& value1, BoundingBox2& value2, BoundingBox2& result)
  pub Boolean Intersects(BoundingBox2 box)
  pub Single Distance(Vector2 point)
  pub ContainmentType Contains(BoundingBox2 box)
  pub ContainmentType Contains(Vector2 point)
  pub BoundingBox2 Translate(Vector2 vctTranlsation)
  pub BoundingBox2 GetIncluded(Vector2 point)
  pub BoundingBox2 Include(Vector2 point)
  pub BoundingBox2 Include(BoundingBox2 box)
```

## 16. Stage orchestration

### `Keen.VRage.Render12.Core.Contracts.ContractsProcessor.ProcessRenderFrame`

```
  SettingsManager.get_System
  RenderCommandBuffer.get_IsWaiting
  Assert.True
  ContractsProcessor.ReplayCommandBuffer
  SceneManager.get_SceneSystems
  Entity.Get
  UISystemComponent.ProcessEnqueuedUIChanges
  ContractsProcessor.ClearFrame
```

### `Keen.VRage.Render12.EngineComponents.Render12EngineComponent.ProcessMessages`

```
  Render12EngineComponent.get_Conc
  RenderThreadManager.AssertRenderThread
  DefinitionPostProcessManager.ApplyDefinitionChanges
  Time.get_RealTime
  ContractsProcessor.ProcessMessageQueue
  ContractsProcessor.get_ProcessedNewFrames
  DeviceWrap.ProcessDebugOutput
```

### `Keen.VRage.Render12.EngineComponents.Render12EngineComponent.IRender_Present`

```
  Render12EngineComponent.get_Conc
  RenderThreadManager.AssertRenderThread
  FrameDispatcher.get_HasRecordedAnything
  Assert.True
  FrameDispatcher.get_IsSharedCopyCommandListCommitted
  Render12EngineComponent.get_RT
  SwapChain.Present
  FrameSpanManager.SynchronizeFrame
  Render12EngineComponent.<IRender_Present>g__GetPrecisionData|53_0
  Render12EngineComponent.<IRender_Present>g__GetPresentStats|53_1
  Time.OnFrameEnd
  CoreSystems.OnFrameEndDisposal
  DeviceWrap.ProcessDebugOutput
```

### Stage types

```
  Keen.VRage.Render12.ClusteringStage
  Keen.VRage.Render12.DebugStage
  Keen.VRage.Render12.GeometryStage.Passes
  Keen.VRage.Render12.LightingStage
  Keen.VRage.Render12.PostProcessStage
  Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection
  Keen.VRage.Render12.PostProcessStage.Upsampling
  Keen.VRage.Render12.PostProcessStage.Upsampling.FSR3
  Keen.VRage.Render12.PostProcessStage.VolumeRendering
  Keen.VRage.Render12.PostProcessStage.Water
  Keen.VRage.Render12.PostProcessStage.Water.WaterEffects
  Keen.VRage.Render12.PrepareStage
  Keen.VRage.Render12.RayTracingStage
  Keen.VRage.Render12.TransparentStage
  Keen.VRage.Render12.UIStage
  Keen.VRage.Render12.UIStage.BatchBase
  Keen.VRage.Render12.UIStage.FontBase
  Keen.VRage.Render12.UIStage.Sprites
  Keen.VRage.Render12.UIStage.Vectors
```

## 17. Who writes the render view

```
Keen.VRage.Render12.Core.Systems.SettingsManager..ctor  writes  _renderView
Keen.VRage.Render12.Core.Systems.SettingsManager..ctor  writes  _previousRenderView
Keen.VRage.Render12.Core.Systems.SettingsManager..ctor  writes  _freezedRenderView
Keen.VRage.Render12.Core.Systems.SettingsManager.SetCameraParameters  writes  _freezedRenderView
Keen.VRage.Render12.Core.Systems.SettingsManager.Keen.VRage.Render12.Core.Systems.IFrameEndDisposalListener.OnFrameEndDisposal  writes  _previousRenderView
```

### Readers of `SettingsManager.RenderView`

```
Keen.VRage.Render12.Utils.DirectRenderMeshConsumer.SubmitTexts
Keen.VRage.Render12.Utils.HierarchicalContainer/StreamingManager.OnCollectStandardsChild
Keen.VRage.Render12.Utils.HierarchicalContainer/RaytracingModelManager.CollectModelEntities
Keen.VRage.Render12.Utils.RenderUtilities.CalculateDistanceToCamera
Keen.VRage.Render12.SceneSystem.RayTracing.RayTracingSceneManager.CreateTLAS
Keen.VRage.Render12.SceneSystem.Components.CullCapacityTrackingComponent.ProximityCheck
Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent.OnAddedToScene
Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent.UpdateCameraPosition
Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent.UpdateVisibility
Keen.VRage.Render12.SceneSystem.Components.LightEntityComponent.UpdatePriority
Keen.VRage.Render12.SceneSystem.Components.LightEntityComponent/ShadowPriorityUpdateContext.BeginWarp
Keen.VRage.Render12.SceneSystem.Components.ManagedTexturePrioritizerComponent.AddOverlayBestFitMipMaps
Keen.VRage.Render12.SceneSystem.Components.ManagedTexturePrioritizerComponent.GetPixelsPerSurfaceMeterBase
Keen.VRage.Render12.SceneSystem.Components.ManagedTexturePrioritizerComponent.OnCollectStandardsRoot
Keen.VRage.Render12.SceneSystem.Components.ParticleLightEntityComponent.UpdateDistanceToCamera
Keen.VRage.Render12.SceneSystem.Components.UISystemComponent/UIBatchRecorder.DrawStringAligned3D
Keen.VRage.Render12.Primitives.RenderViewSlim.op_Implicit
Keen.VRage.Render12.PrepareStage.CullingSetup.Compose
Keen.VRage.Render12.PrepareStage.DrawCommandsGenerationJob.DoWork
Keen.VRage.Render12.PrepareStage.GrassRendering.DoWork
Keen.VRage.Render12.PostProcessStage.FlaresContext.GetFlareConstants
Keen.VRage.Render12.PostProcessStage.HBAOJob.CreateConstantBuffersData
Keen.VRage.Render12.PostProcessStage.Water.WaterContext/ProjectionContext.UpdateProjections
Keen.VRage.Render12.PostProcessStage.Water.WaterJob.BeginFrame
Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.PrepareDraw
Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.DrawGrids
Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.DrawCrossSectionDepths
Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.GetSunRenderView
Keen.VRage.Render12.PostProcessStage.Water.WaterMeshJob.ComputeInsideMask
Keen.VRage.Render12.PostProcessStage.Water.WaterShadingJob.DoWork
Keen.VRage.Render12.PostProcessStage.Water.WaterEffects.WaterParticlesJob.DoWork
Keen.VRage.Render12.PostProcessStage.Upsampling.FSR3.FSR3.Draw
Keen.VRage.Render12.PostProcessStage.Upsampling.FSR3.FSR3_1.Draw
Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection.ScreenSpaceReflections.CreateCbuffer
Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection.ScreenSpaceReflections.DoWork
Keen.VRage.Render12.LightingStage.Cascade.GetUpdateInfo
Keen.VRage.Render12.LightingStage.Cascade.ComputeCascadeView
Keen.VRage.Render12.LightingStage.CascadeShadowsContext.FlushUpdates
Keen.VRage.Render12.LightingStage.CascadeShadowsContext.CheckCameraShifted
Keen.VRage.Render12.LightingStage.CharacterShadowCascade.GetUpdateInfo
Keen.VRage.Render12.LightingStage.CharacterShadowsContext.FlushUpdates
Keen.VRage.Render12.LightingStage.DirectionalLightShadowResources.OnBeginDraw
Keen.VRage.Render12.LightingStage.DirectionalLightShadowResources.CreateIndirectSetupConstantBuffer
Keen.VRage.Render12.LightingStage.EnvironmentProbeManager.PrepareProbes
Keen.VRage.Render12.LightingStage.EnvironmentProbeManager.UpdateLocalLightAmbient
Keen.VRage.Render12.LightingStage.EnvironmentProbeManager/Render..ctor
Keen.VRage.Render12.LightingStage.PlanetEnvironmentManager.ComputeSkyboxBrightnessMultiplier
Keen.VRage.Render12.GeometryStage.Passes.CullingJob.DoWork
Keen.VRage.Render12.Core.CoreSystems.UpdateDebugDrawRoot
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.UpdateSurfels
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.UpscaleTargetFSR
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderHighlightsAndTransparentUnlit
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.MainViewCulling
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.PrepareClusters
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.ProcessParticles
Keen.VRage.Render12.Core.Systems.ScreenBuffers.GetCurrentFrameRenderTarget
Keen.VRage.Render12.Core.Systems.CommonResources.PlanetEnvironmentGroup.OnBeginDraw
Keen.VRage.Render12.Core.Systems.CommonResources.PlanetEnvironmentGroup.GetCloudShadowSetup
Keen.VRage.Render12.Core.Systems.CommonResources.SettingsGroup.CreateCameraSettings
Keen.VRage.Render12.Core.Systems.CommonResources.WeatherModifiersCullingContext.InsertWeatherModifiers
(60 readers)
```

## 18. SceneDrawSystem

```
  field ListenerCollector _listeners
  field CullingGeometryJob _cullingJobMainViewEffects
  field VisibleEntitiesUpdateJob _visibleEntitiesUpdateJob
  field VisibleEntitiesUpdateJob _visibleInstancedEntitiesUpdateJob
  field LODStateUpdateJob _lodStateUpdateJob
  field LODStateUpdateJob _instancedLodStateUpdateJob
  field CullingJob _mainViewCullingJob
  field CullingJob _indirectCullingJob
  field CullingJob _cascadeCullingJob
  field CullingJob _characterShadowsCullingJob
  field CullingJob _localShadowsCullingJob
  field CullingJob _localShadowMasksCullingJob
  field CascadeShadowsMergeJob _cascadeShadowsMergeJob
  field DepthPyramidJob _depthPyramidJob
  field DepthPassJob _shadowsDepthPass
  field DepthPrePassJob _depthPrePass
  field TerrainBlendingJob _terrainBlending
  field GBufferPassJob _gBufferPass
  field DeferredTexturingJob _deferredTexturingPass
  field GBufferDecalPassJob _gBufferDecalPass
  field DecalSortingPassJob _gSortDecalPass
  field LocalLightSortingPassJob _gSortLocalLightPass
  field HologramPassJob _hologramPass
  field TransparentPassJob _transparentPass
  field UnlitPassJob _unlitPass
  field HighlightJob _highlightJob
  field TransparentPassJob _transparentUnlitPass
  field TopMostPassJob _topMostPass
  field IndirectEnvironmentPassJob _indirectEnvironmentPass
  field SkyboxMotionVectorsJob _skyboxMotionVectorsJob
  field WaterJob _waterPass
  field WaterMeshJob _waterMeshJob
  field SurfelGenerationJob _surfelGenerationJob
  field WaterParticlesJob _waterParticlesJob
  field ClusteringJob _clusterJob
  field MipMapPreFilterJob _mipMapPreFilterJob
  field DirectionalLightShadowJob _directionalLightShadowJob
  field DirectionalLightJob _directionalLightJob
  field AmbientLightJob _ambientLightJob
  field LocalLightsJob _localLightsJob
  pub Void .ctor()
  pub Void InitRTXJobs()
  pub Void Dispose()
  pub Boolean ShouldDraw()
  pub Void Draw(ResizableRWRenderTargetTexture finalLDRBuffer)
  int Void BeginAsyncComputeScope()
  int Void EndAsyncComputeScope()
  pub Void ExecuteAccelerationStructuresBuilding()
  int Void ExecuteRaytracingPrepareAndSceneFinalize()
  int Void RaytracingPrepare(DirectCommandList commandList)
  int Void SceneFinalize(DirectCommandList commandList)
  pub Void DrawRenderViewFrustum(RenderView& view)
  pub Void SetRandomSeed(Int32 seed)
  pub Void OnUpdateStats()
  pub Void OnFrameEndDisposal()
  pub Void OnResetContext()
  int Void UpdateSurfels(DirectCommandList commandList)
  int Void ExecuteForwardAndPostProcess(ResizableRWRenderTargetTexture lBuffer, Nullable`1& screenshotTexture, ResizableRWRenderTargetTexture finalLDRBuffer)
  int Void ExecuteForwardPasses(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ExecutePostPasses(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer, ResizableRWRenderTargetTexture lBuffer, Nullable`1& screenshotTexture, Boolean saveScreenshotWithoutUi)
  int Void ComputeExposure(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, ITexture2DView& exposure, Nullable`1& debugHistogram)
  int Void PatchHoles(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ProcessPreUpscaleDebugView(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void UpscaleTargetFSR(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer, ResizableRWRenderTargetTexture lBuffer, ITexture2DView exposure, Nullable`1& tempLDRBuffer, Nullable`1& tempHDRBuffer, ResizableRWRenderTargetTexture& toneMappingInput, ResizableRWRenderTargetTexture& toneMappingOutput)
  int Void ApplyBloom(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingInput, ITexture2DView exposure, Borrowed`1& bloom)
  int Void ApplyToneMapping(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingInput, ResizableRWRenderTargetTexture toneMappingOutput, ITexture2DView exposure, ResizableRenderTargetTexture bloom)
  int Void ProcessPostUpscaleDebugView(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingOutput, ResizableRWRenderTargetTexture lBuffer, ResizableRWRenderTargetTexture finalLDRBuffer)
  int Void ApplyNonFSRUpscalingAndAA(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingOutput, ITexture2DView exposure, ResizableRWRenderTargetTexture finalLDRBuffer)
  int Borrowed`1 SaveScreenshot(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer)
  int Void DrawUI(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer)
  int Void ReturnBorrowedAndReadbackCounters(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer, Nullable`1 tempLDRBuffer, Nullable`1 tempHDRBuffer, Nullable`1 debugHistogram)
  int Void ExecuteWaterPrepass(DirectCommandList commandList)
  int Void DrawWater(DirectCommandList commandList, Nullable`1 fsrMasks, IRenderTargetView accumulationBuffer, IRenderTargetView coverageBuffer, ResizableRWRenderTargetTexture lBuffer)
  int Void ExecuteVolumetricPasses(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, OITBuffers oitBuffers, Nullable`1 fsrMasks)
  int Void DrawUnlit(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Boolean CheckDebugViewInForward(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ComputeSSR(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void RenderTransparent(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, OITBuffers oitBuffers, Nullable`1 fsrMasks)
  int Void RenderHighlightsAndTransparentUnlit(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, OITBuffers oitBuffers, Nullable`1 fsrMasks)
  int Boolean RenderHolograms(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, OITBuffers oitBuffers, Nullable`1 fsrMasks)
  int Void ResolveStochasticTransparency(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ResolveOIT(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, OITBuffers oitBuffers, Nullable`1 fsrMasks)
  int Void RenderFlares(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, OITBuffers oitBuffers, Nullable`1 fsrMasks)
  int Void ExecuteLighting(ResizableRWRenderTargetTexture lBuffer)
  int Void UpdateAtmosphere(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ComputeDirectionalLighting(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ComputeLocalLights(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, ResizableRenderTargetTexture localLightDiffuseBuffer)
  int Void ComputeCloudShadows(DirectCommandList commandList)
  int Void DrawSkybox(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ComputeGI(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer, ResizableRenderTargetTexture localLightDiffuseBuffer)
  int Void ApplyAtmosphere(DirectCommandList commandList, ResizableRWRenderTargetTexture lBuffer)
  int Void ExecuteScenePreparationAndRender(Vector2I finalResolution)
  int Void UpdateVideoPlayer(DirectCommandList commandList)
  int Void ScenePreparationAndRender(DirectCommandList commandList, Vector2I finalResolution)
  int Void ScenePreparation(DirectCommandList commandList, Vector2I finalResolution)
  int Void EnsureRangesOutputGeometryBuffers(DirectCommandList commandList)
  int Void RenderShadows(DirectCommandList commandList)
  int Void RenderEnvironmentProbe(DirectCommandList commandList)
  int Void ExecuteEnvironmentProbeUpdate(DirectCommandList commandList, Request& request)
  int Void MainViewCulling(DirectCommandList commandList, Boolean isFirstPass)
  int Void RenderGrass(DirectCommandList commandList, Boolean hzboMainViewEnabled)
  int Void RenderGBuffer(DirectCommandList commandList, Nullable`1 fsrMasks, Boolean isFirstPass, Boolean hzboMainViewEnabled)
  int Void RenderMainView(DirectCommandList commandList)
  int Void BuildHiZBuffer(DirectCommandList commandList, OcclusionContext occlusionContext, ITexture2DView depthTexture)
  int Void PrepareClusters(DirectCommandList commandList)
  int Void ProcessParticles(DirectCommandList commandList)
  int Void RenderDecals(DirectCommandList commandList)
  int Void RenderShadowCascades(DirectCommandList commandList)
  int Void RenderCharacterShadows(DirectCommandList commandList)
  int Void RenderLocalLightShadows(DirectCommandList commandList)
  int Void RenderPendingIBL(DirectCommandList commandList)
  int Void ExecuteHBAO(DirectCommandList commandList)
```

## 19. Where the scene's output target comes from

Methods that bind render targets (`OMSetRenderTargets` /
`SetRenderTargets` / `ClearRenderTargetView`) and where the target
value originates — parameter, field, or frame-global.

```
WaterShadingJob.DoWaterStochastic  fromParam=True
      get:ScreenBuffers.DepthStencilBuffer
      get:ResizableDepthStencilTexture.DepthTexture
      get:DirectionalLightShadowResources.DepthMaps
      get:LocalLightsManager.SingleShadowMaps
      get:LocalLightsManager.CubeShadowMaps
      get:LocalLightsManager.CubeShadowMasks
DirectCommandList.SetupRenderTargets  fromParam=False
DirectCommandList.PrepareDraw  fromParam=False
```

## 19b. ScreenBuffers

### `Keen.VRage.Render12.Core.Systems.ScreenBuffers`

```
  pub field Format HDR_FORMAT
  pub field Format LDR_FORMAT
  pub field Format GBUFFER0_FORMAT
  pub field Format GBUFFER1_FORMAT
  pub field Format GBUFFER2_FORMAT
  pub field Format GBUFFER3_FORMAT
  pub field Format GBUFFER4_FORMAT
  pub field Format VBUFFER0_FORMAT
  pub field Format VBUFFER1_FORMAT
  pub field Format VBUFFER2_FORMAT
  pub field Format VBUFFER3_FORMAT
  pub field Format VBUFFER4_FORMAT
  pub field Format OIT_ACCUM_FORMAT
  pub field Format OIT_COVERAGE_FORMAT
  pub field Format SHADOW_PASS_FORMAT
  pub field Format DEPTH_RT_FORMAT
  pub field Format FSR_REACTIVE_FORMAT
  pub field Format FSR_TRANSCOMP_FORMAT
  int field Format[] _gBufferFormats
  int field Format[] _vBufferFormats
  int field Vector2I _usedMaxResolution
  int field Vector2I <PreUpscaleResolution>k__BackingField
  int field Vector2I <PrevPreUpscaleResolution>k__BackingField
  int field ResizableDepthStencilTexture _depthStencilBuffer
  int field ResizableRWRenderTargetTexture[] <GBuffer>k__BackingField
  int field ResizableRWRenderTargetTexture <FinalLDRTexture>k__BackingField
  int field ResizableRWRenderTargetTexture <FinalLDRPlaceholder>k__BackingField
  int field Int32 _cameraJumpWaitFrameId
  int field Int32 _screenshotWaitFrameId
  prop Vector2I MaxPreUpscaleResolution
  prop Vector2I PreUpscaleResolution
  prop Vector2I PrevPreUpscaleResolution
  prop ResizableDepthStencilTexture DepthStencilBuffer
  prop ResizableRWRenderTargetTexture[] GBuffer
  prop ResizableRWRenderTargetTexture FinalLDRTexture
  prop ResizableRWRenderTargetTexture FinalLDRPlaceholder
  prop Format[] GBufferFormats
  prop Format[] VBufferFormats
  pub Void .ctor()
  int Void InitializeBuffers(Vector2I&)
  pub Void Dispose()
  int Void DisposeBuffers()
  pub Void Update(CopyCommandList, Vector2I&, Vector2I&)
  int Void CreateBackbufferPlaceholder()
  int Void TryDisposeBackbufferPlaceholder()
  pub ResizableRWRenderTargetTexture GetGBuffer(GBufferIndex)
  pub Format GetGBufferFormat(GBufferIndex)
  pub ResizableRWRenderTargetTexture GetCurrentFrameRenderTarget()
  pub Boolean ReadyToTakeScreenshot()
```

### `GBuffer` — not found

### Fields typed ScreenBuffers

```
Keen.VRage.Render12.Core.CoreSystems.ScreenBuffers static
```

## 20. RenderCommandBuffer — what can be sent to the renderer

### `Keen.VRage.Render.FrameData.RenderCommandBuffer`

```
  pub field Int32 PAGE_SIZE
  int field RenderCommandBuffer _currentThreadOverride
  int field StringId <DebugName>k__BackingField
  int field AtomicFlag _isCommitted
  int field ManualResetEventSlim _spinSleep
  prop RenderCommandBuffer Default
  prop StringId DebugName
  prop Boolean IsCommitted
  prop Boolean IsWaiting
  pub Void Dispose()
  pub Void Commit()
  pub Void HintWakeup()
  pub Void ActivateOnThisThread()
  pub Void DeactivateOnThisThread()
  int Void AssertNoCommandsToCommittedBuffer()
  pub Int32 ReplayAll(ValueTuple`2[] handlers, Stopwatch sleepTimer, Stopwatch firstSleepTimer)
  pub Boolean ReplayPartial(ValueTuple`2[] handlers, ReplayToken token, ReplayBudget budget, Boolean consumeAllCommands, Stopwatch sleepTimer, Stopwatch firstSleepTimer)
  int Boolean ReplayInternal(ValueTuple`2[] handlers, IntPtr& totalBytesProcessed, ReplayToken token, ReplayBudget budget, Boolean consumeAllCommands, Stopwatch sleepTimer, Stopwatch firstSleepTimer)
  int IntPtr Replay_Impl(ValueTuple`2[] handlers, Byte& dataPtr, IntPtr offset, IntPtr activeBytes, Int64 endTime)
  pub Void Clear(Int32 averageBufferSizeHint)
  pub Void AreaLightEntity_SetParameters(RenderId thiz, ColorLinear lightIntensityRGB, Vector2 dimensions, Single barnAngle, Single barnLength, ResourceHandle imageTexture)
  pub Void IChildEntity_SetParent(RenderId thiz, RenderId parent, Nullable`1 childToParent)
  pub Void IChildEntity_UpdateTransform(RenderId thiz, RelativeTransform localTransform)
  pub Void IChildEntity_SetDebugDrawEnable(RenderId thiz, Boolean value)
  pub Void CapsuleLightEntity_SetParameters(RenderId thiz, ColorLinear lightIntensityRGB, Single lineLength, Single radius)
  pub Void DecalEntity_FadeOutInternal(RenderId thiz, TimeSpan start, TimeSpan end)
  pub Void DecalEntity_SetFlags(RenderId thiz, DecalFlags flags)
  pub Void DecalEntity_ChangeParent(RenderId thiz, RenderId newRoot)
  pub Void DecalEntity_DisableDecal(RenderId thiz)
  pub Void DecalEntity_SetMaterial(RenderId thiz, DecalMaterialDefinition material)
  pub Void DecalEntity_SetSize(RenderId thiz, Vector3 material)
  pub Void DecalEntity_SetFalloff(RenderId thiz, DecalFalloff falloff)
  pub Void DecalEntity_EnableDecal(RenderId thiz, RelativeTransform newTransform, DecalEntityParentMethod parentMethod, DecalMaterialDefinition material, DecalCreationParameters parameters)
  pub Void DecalSystem_DestroyDecal(RenderId id)
  pub Void DecalSystem_AddDecal(RenderId id, String debugName, RelativeTransform localTransform, DecalMaterialDefinition decalMaterial, DecalEntityParentMethod parentMethod, DecalCreationParameters parameters)
  pub Void FloraSectorEntity_RemoveInstance(RenderId thiz, Int16 instanceId)
  pub Void FloraSectorEntity_AddInstance(RenderId thiz, FloraInstance floraInstance)
  pub Void FloraSystem_DestroyFloraSector(RenderId id)
  pub Void FloraSystem_DisposeSectorAfterBuilt(RenderId id)
  pub Void FloraSystem_AddFloraSector(RenderId id, String debugName, RenderId rootEntity, Buffer`1 floraInstances, WorldTransform planetTransform)
  pub Void GrassEntity_UpdateModel(RenderId thiz, ResourceHandle modelResourceHandle, Buffer`1 grassMaterialsUsed, Int32 lod)
  pub Void IEntityFade_FadeOutInternal(RenderId thiz, TimeSpan start, TimeSpan end)
  pub Void IEntityFade_FadeInInternal(RenderId thiz, TimeSpan start, TimeSpan end)
  pub Void GravityProbeRenderEntity_AttachEffect(RenderId thiz, RenderId effectEntity, Vector3 gravity)
  pub Void GravityProbeRenderEntity_UpdateGravity(RenderId thiz, Vector3 gravity)
  pub Void InstancedModelEntity_SetRenderFlags(RenderId thiz, RenderFlags flags)
  pub Void InstancedModelEntity_SetRenderFlagsState(RenderId thiz, RenderFlags flag, Boolean state)
  pub Void InstancedModelEntity_SetOpacity(RenderId thiz, Single opacity)
  pub Void InstancedModelEntity_SetInstanceData(RenderId thiz, GeneratedResourceHandle instanceData, BoundingBox boundingBox)
  pub Void InstancedModelEntity_SetBoundingBox(RenderId thiz, BoundingBox bbox)
  pub Void InstancedModelEntity_SetEntityCustomData(RenderId thiz, CustomGPUDataPayload instanceData)
  pub Void IEntityMaterials_SetAllPartsVisible(RenderId thiz)
  pub Void IEntityMaterials_SetPartVisibility(RenderId thiz, StringId meshPart, Boolean visible)
  pub Void IEntityMaterials_SetVisibleParts(RenderId thiz, ImmutableArray`1 parts)
  pub Void IEntityMaterials_ResetMaterialStates(RenderId thiz)
  pub Void IEntityMaterials_SetMaterial(RenderId thiz, StringId meshPart, MaterialDefinition material)
  pub Void IEntityMaterials_SetMaterials(RenderId thiz, ImmutableArray`1 pairs)
  pub Void IEntityMaterials_ResetMaterials(RenderId thiz)
  pub Void IEntityMaterials_ResetMaterial(RenderId thiz, StringId meshPart)
```

### `Keen.VRage.Render.FrameData.RenderDrawCommandBuffer`

```
  pub field String DebugString
  int field GeneratedResourceHandle <RenderTarget>k__BackingField
  int field UInt32 <Version>k__BackingField
  prop GeneratedResourceHandle RenderTarget
  prop UInt32 Version
  pub Void Replay(ValueTuple`2[] replayTable)
  pub Boolean IsEmpty()
  pub Void Clear()
  pub String ToString()
  pub Void IDrawBatch_DrawString(Font font, Vector2 screenCoord, ColorSRGB colorMask, String text, Single screenScale, Boolean ignoreBounds, Nullable`1 maxTextWidth, Single rotation)
  pub Void IDrawBatch_DrawSubstring(Font font, Vector2 screenCoord, ColorSRGB colorMask, ReadOnlySpan`1 text, Single screenScale, Boolean ignoreBounds, Nullable`1 maxTextWidth)
  pub Void IDrawBatch_DrawStringAligned(Font font, Vector2 screenCoord, ColorSRGB colorMask, String text, Single fontScale, Boolean ignoreBounds, Nullable`1 maxTextWidth, TextAlignmentEnum align)
  pub Void IDrawBatch_DrawStringAligned3D(Font font, Vector3 textCoord, ColorSRGB colorMask, String text, Single fontScale, Boolean ignoreBounds, Nullable`1 rootEntity, Nullable`1 maxTextWidth, TextAlignmentEnum align)
  pub Void IDrawBatch_DrawLine(Vector2 from, Vector2 to, ColorSRGB color, Single width, DashingTypeEnum dashingType, Single dashingScale, Boolean ignoreBounds)
  pub Void IDrawBatch_DrawPath(ReadOnlySpan`1 splines, ColorSRGB strokeColor, Single strokeWidth, Boolean ignoreBounds)
  pub Void IDrawBatch_DrawPathExt(ReadOnlySpan`1 splines, ColorSRGB strokeColor, Single strokeWidth, ReadOnlySpan`1 dashesAndGaps, Single dashOffset, LineCapEnum lineCap, LineJoinEnum lineJoin, Single miterLimit, Boolean ignoreBounds)
  pub Void IDrawBatch_DrawFill(ReadOnlySpan`1 splines, ColorSRGB primaryColor, Nullable`1 gradientFill, Boolean ignoreBounds)
  pub Void IDrawBatch_DrawImage(ResourceHandle texture, BoundingBox2& destination, ColorSRGB color, Boolean ignoreBounds, Nullable`1 maskTexture, Nullable`1& sourceRectangle)
  pub Void IDrawBatch_DrawImageExt(ResourceHandle texture, BoundingBox2& destination, ColorSRGB color, Vector2 rotationPivot, Single rotation, Boolean ignoreBounds, Single rotationSpeed, Nullable`1 maskTexture, Nullable`1& sourceRectangle)
  pub Void IDrawBatch_DrawVideoExt(RenderId videoPlayerRenderId, BoundingBox2I& destination)
  pub Void IDrawBatch_ScissorPush(BoundingBox2I screenRectangle)
  pub Void IDrawBatch_ScissorPop()
```

## 21. CoreSystems — the swappable globals

```
  int static field TimeSpan _disposeTimeout
  int static field Boolean <EarlyTaskExit>k__BackingField
  int static field Boolean <AreTasksCancelled>k__BackingField
  int static field Object <RenderLifetime>k__BackingField
  int static field Thread <RenderThread>k__BackingField
  int static field Boolean <IsInited>k__BackingField
  int static field Boolean <IsUnderutilizationDetected>k__BackingField
  int static field Boolean <IsRecompilationSucceeded>k__BackingField
  pub static field Log Log
  pub static field ContractsProcessor Messages
  pub static field FrameSpanManager FrameSpan
  pub static field Time Time
  pub static field ShaderFileReaderManager ShaderFileReaders
  pub static field Adapters Adapters
  pub static field DeviceWrap DeviceWrap
  pub static field ObjectPoolMonitor ObjectPoolMonitor
  pub static field SimpleObjectPool SimpleObjectPool
  pub static field AllocLog AllocLog
  pub static field SettingsManager Settings
  pub static field MemoryHierarchy MemoryHierarchy
  pub static field StreamingStatManager StreamingStats
  pub static field VideoMemoryMonitor VideoMemoryMonitor
  pub static field DeviceContext DeviceContext
  pub static field GPUResourcePool GPUResourcePool
  pub static field StatManager Stats
  pub static field SequenceReportManager RecordedActionSequenceReports
  pub static field SequenceReportManager ReplayedActionSequenceReports
  pub static field RenderIdManager RenderIds
  pub static field FramePacer FramePacer
  pub static field FrameDispatcher FrameDispatcher
  pub static field DataUploader DataUploader
  pub static field DirectStorage DirectStorage
  pub static field D3DHeapManager D3DHeap
  pub static field BlendStateManager BlendStates
  pub static field DepthStencilStateManager DepthStencilStates
  pub static field RasterizerStateManager RasterizerStates
  pub static field SamplerManager Samplers
  pub static field InputLayoutManager InputLayouts
  pub static field ShaderFileCacheManager ShaderFileCache
  pub static field ShaderManager Shaders
  pub static field RootSignatureManager RootSignatures
  pub static field GraphicsPSOManager GraphicsPSOs
  pub static field ComputePSOManager ComputePSOs
  pub static field RayTracingPSOManager RayTracingPSOs
  pub static field DescriptorHeapPool DescriptorHeap
  pub static field Texture2DTableManager Texture2DTables
  pub static field RWTexture2DTableManager RWTexture2DTables
  pub static field TextureCubeTableManager TextureCubeTables
  pub static field StructuredBufferTableManager StructuredBufferTables
  pub static field BindableBufferManager BindableBuffers
  pub static field RASBufferManager RASBuffers
  pub static field TemporaryBufferManager TemporaryBuffers
  pub static field ClearingManager ClearingManager
  pub static field BindableTextureManager BindableTextures
  pub static field BindableTexturePoolManager BindableTexturePool
  pub static field ManagedROBufferManager ManagedROBuffers
  pub static field ManagedRuntimeBufferManager ManagedRuntimeBuffers
  pub static field CommandSignatureManager CommandSignatures
  pub static field QueryHeapManager QueryHeaps
  pub static field LocalLightsManager LocalLights
  pub static field EnvironmentProbeManager EnvironmentProbeManager
  pub static field DebugReadbackManager DebugReadback
  pub static field GPUProfiler GPUProfiler
  pub static field ResourceStateMonitor ResourceStateMonitor
  pub static field ShaderAssertsManager ShaderAsserts
  pub static field LoadingMonitor LoadingMonitor
  pub static field SwapChain SwapChain
  pub static field UnifiedIndexBuffer ModelUIB
  pub static field SparseUpdateDataManager SparseUpdateData
  pub static field ScreenBuffers ScreenBuffers
  pub static field GPUStats GPUStats
  pub static field GPUFrameManager GPUFrameManager
  pub static field FrameUploadManager FrameUploadManager
  pub static field GPUSceneManager GPUScene
  pub static field ModelManager ModelManager
  pub static field RenderCommandBatchManager ParallelBatchManager
  pub static field ResourceUploadSynchronizationManager ResourceUploadSynchronizationManager
  pub static field MaterialRootSignatureManager MaterialRootSignatures
  pub static field Manager RuntimeBufferEntities
  pub static field WeatherModifiersAllocator CloudsModifierAllocator
  pub static field MaterialsManager Materials
  pub static field HierarchicalContainer HierarchicalContainer
  pub static field RayTracingBLASManager RayTracingBLASManager
  pub static field RayTracingSceneManager RayTracingScene
  pub static field SceneManager Scene
  pub static field ManagedTextureStreamingComponent ManagedTextureStreaming
  pub static field ManagedTexturePrioritizerComponent ManagedTexturePrioritizer
  pub static field DistanceTagManagerComponent DistanceTagManager
  pub static field ParticleSystemComponent ParticleSystem
  pub static field ParticleEffectManagerComponent ParticleEffectManager
  pub static field ManagedTextureManagerComponent ManagedTextures
  pub static field ManagedTexturePinManagerComponent ManagedTexturePinManager
  pub static field CullCapacityTrackingManagerComponent CullCapacityTrackingManager
  pub static field DecalSystemComponent Decals
  pub static field FloraSystemComponent FloraSystem
  pub static field MeshEffectSystemComponent MeshEffectSystem
  pub static field ImpostorManagerComponent ImpostorManager
  pub static field WaterSystemComponent Water
  pub static field ImpostorBakingManager ImpostorBakingManager
  pub static field IBLManager IBLs
  pub static field PlanetEnvironmentManager PlanetEnvironments
  pub static field AtmosphereManager Atmospheres
  pub static field CloudsManager Clouds
  pub static field CommonResourcesManager CommonResources
  pub static field IRCacheResourcesManager IRCacheResources
  pub static field RaytraceGIResourcesManager RaytraceGIResources
  pub static field FontManager Fonts
  pub static field VectorImageManager VectorImages
  pub static field OffscreenTargetManager OffscreenTarget
  pub static field DrawContextManager DrawContexts
  pub static field DefinitionPostProcessManager DefinitionPostProcesses
  pub static field DirectRenderMeshBuilderFactory MeshBuilderFactory
  pub static field MainUISystem MainUISystem
  pub static field OffscreenUIRenderer OffscreenUIRenderer
  pub static field SparseUpdateJob SparseUpdateJob
  pub static field SpriteRenderer SpriteRenderer
  pub static field VectorRenderer VectorRenderer
  pub static field DebugPassJob DebugPassJob
  pub static field ScreenshotsManager ScreenshotsManager
  pub static field CopyJob CopyLDRJob
  pub static field GeometryPSOCache GeometryPSOCache
  pub static field SceneDrawSystem SceneDrawSystem
  pub static field CrashGPUJob CrashGPUJob
  int static field ListenerCollector _listeners
  int static field MeshBuilder _globalMeshBuilder
  int static field MeshBuilder2D _globalMeshBuilder2D
  int static field RootEntityComponent _debugDrawRoot
  int static field Nullable`1 _traceChannel
  prop Boolean EarlyTaskExit setter=int
  prop Boolean AreTasksCancelled setter=int
  prop Object RenderLifetime setter=none
  prop Thread RenderThread setter=int
  prop Boolean IsInited setter=int
  prop Boolean IsUnderutilizationDetected setter=int
  prop Boolean IsRecompilationSucceeded setter=pub
  prop TraceChannel TraceChannel setter=none
  prop MeshBuilder DebugDraw setter=none
  prop MeshBuilder2D DebugDraw2D setter=none
  pub Void InitializeSyncSystems(ProjectManagerEngineComponent, RenderObjectBuilder)
  pub Void Initialize(RenderDisplaySettings, IPlatformWindows, RenderConfiguration)
  pub Void Dispose()
  pub Void InitFramePacer(Int32, AppLoop)
  int CustomQueuePump CreateBlockingPump()
  pub Void PrintAllModules(Log)
  pub Void AssertRenderThread()
  pub Void CheckRenderThread()
  pub Void UpdateStats()
  pub Void FinalizeResources()
  pub Void OnFrameEndDisposal()
  pub Void CompactMemory()
  pub Void OnResetContext()
  pub Void UpdateDebugDrawRoot()
  int Void .cctor()
```

## 22. What orchestrates the scene draw

Methods that touch three or more distinct render stages — the
orchestrators, whatever they are called.

```
Keen.VRage.Render12.PostProcessStage.HighlightJob.DoWork   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.PostProcessStage.Water.SurfelGenerationJob.Generate   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.PostProcessStage.Water.SurfelGenerationJob.RasterizeAxis   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.GeometryStage.Passes.HologramPassJob.DoWork   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.EngineComponents.Render12EngineComponent.<Draw>g__DrawInternal|52_0   [PostProcessStage, PrepareStage, UIStage]
Keen.VRage.Render12.Core.CoreSystems.Initialize   [LightingStage, PostProcessStage, PrepareStage, UIStage]
Keen.VRage.Render12.Core.Systems.DrawContextManager.CreateInitialContexts   [LightingStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.DrawContextManager.DisposeContexts   [LightingStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem..ctor   [GeometryStage, LightingStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.DrawUI   [GeometryStage, PrepareStage, UIStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.ExecuteVolumetricPasses   [LightingStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.DrawUnlit   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderTransparent   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderHighlightsAndTransparentUnlit   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderHolograms   [GeometryStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.ExecuteEnvironmentProbeUpdate   [GeometryStage, LightingStage, PostProcessStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderShadowCascades   [GeometryStage, LightingStage, PrepareStage]
Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderCharacterShadows   [GeometryStage, LightingStage, PrepareStage]
```

## 23. Stage entry points and their state

```
CrashGPUJob   (fields: 2)
    pub Void DoWork(ComputeCommandList commandList)
CulledGeometrySortJob   (fields: 4)
    pub Void DoWork(ComputeCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, Boolean isFirstPass)
CullingEntityProxyJob   (fields: 6)
    pub Void DoWork(ComputeCommandList commandList, EntityProxyContext targetContext, OutputGeometryBufferContext outputGeometryBuffers, VisibilityListBufferContext visibilityListBufferContext, RenderViewSlim viewSlim, OcclusionContext occlusionContext, Boolean isFirstPass, Nullable`1& posViewToNegViewProj, Nullable`1 baseRenderView, Int32 rootEntityId, Boolean show3DMap, CharacterCullingBehavior characterCullingBehavior, Int32 cascadeIndex)
CullingGeometryJob   (fields: 7)
    pub Void DoWork(ComputeCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, VisibilityListBufferContext visibilityListBufferContext, RenderViewSlim viewSlim, PassLODSettings passLODSetting, Nullable`1& posViewToNegViewProj, Boolean wasViewMoveSmooth, LODTransitionContext lodTransitions, OcclusionContext occlusionContext, Boolean isFirstPass, Nullable`1 baseRenderView, Int32 rootEntityId, Boolean hideUI, Boolean show3DMap, CharacterCullingBehavior characterCullingBehavior)
CullingSettings   (fields: 6)
    pub CullingSettings Compose(Boolean isForMainView, Boolean occlusionCulling, Nullable`1 proxyType, Boolean isEffectsPass, Boolean isLocalLights)
CullingSetup   (fields: 24)
    pub CullingSetup Compose(RenderViewSlim& viewSlim, Int32 allEntities, Int32 visibleEntitiesCapacity, Int32 rootEntitiesCount, Int32 subPartCount, Int32 modelsCount, UInt32 passMask, Int32 smallObjectVisibleMult, Boolean isFirstPass, Nullable`1& posViewToNegViewProj, Nullable`1& commonRenderView, Int32 rootEntityId, Boolean hideUI, Boolean show3DMap, Boolean disableMeshNormalCulling, CharacterCullingBehavior characterCullingBehavior, Int32 cascadeIndex)
DepthPyramidJob   (fields: 3)
    pub Void DoWork(DirectCommandList commandList, OcclusionContext mainOcclusion, ITexture2DView depthTexture)
DrawCommandsGenerationJob   (fields: 4)
    pub Void DoWork(ComputeCommandList commandList, EntityProxyContext proxyContext, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, RenderViewSlim& viewSlim, PassLODSettings passLODSetting, Boolean wasViewMoveSmooth, LODTransitionContext lodTransitions)
GrassGenerationCommandsCreationJob   (fields: 5)
    pub Void DoWork(ComputeCommandList commandList, EntityProxyContext entityProxyContext, OutputGeometryBufferContext outputGeometryBuffers, GrassBufferContext grassBufferContext, GrassSettings grassSettings)
GrassRendering   (fields: 14)
    pub Void DoWork(DirectCommandList commandList, GrassBufferContext grassBufferContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView[] renderTargets, ResizableDepthStencilTexture depthTarget, ResizableRWRenderTargetTexture hizBuffer, GrassSettings grassSettings, EntityProxyContext culledProxies)
InstancedModelEntitiesMeshBakingJob   (fields: 2)
    pub Void DoWork(ComputeCommandList commandList, Int32 instanceCount, GPUBufferId instanceBufferId, Int32 indexCountPerInstance, Int32 startIndexLocation, GPUBufferId vertexStream0Id, GPUBufferId vertexStream1Id, GPUBufferId vertexStream2Id, Int32 materialParamsIndex, Int32 entityCustomDataId, RWBuffer outputVertices, Int32 outputVerticesOffset)
IRCacheInitializeJob   (fields: 2)
    pub Void DoWork(ComputeCommandList commandList)
LODSetup   (fields: 6)
    pub LODSetup Compose(PassLODSettings& passSettings, Boolean lastUpdateWasSmooth)
LODStateUpdateJob   (fields: 8)
    pub Void DoWork(ComputeCommandList commandList, LODTransitionContext lodTransitionContext)
SparseUpdateJob   (fields: 11)
    pub Void DoWork(ComputeCommandList commandList, SparseUpdateData updateData)
VertexTransferJob   (fields: 2)
    pub Void DoWork(ComputeCommandList commandList, GPUBufferId vertexStream0Id, GPUBufferId vertexStream1Id, GPUBufferId vertexStream2Id, Int32 materialParamsIndex, Int32 entityCustomDataId, BufferRange indicesRange, IRWStructuredBufferView outputVertices)
VisibleEntitiesUpdateJob   (fields: 6)
    pub Void DoWork(ComputeCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers)
AmbientLightJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView rtView, ITexture2DView giBufferDiffuse, ITexture2DView giBufferSpecular)
AtmosphereAdditiveJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView rtView)
AtmosphereLUTJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView LUTTarget, AtmosphereConstants& atmosphereConstants)
AtmosphereMultiplyJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView rtView)
CascadeShadowsMergeJob   (fields: 1)
    int Void DoWork(DirectCommandList commandList)
CascadeStatsJob   (fields: 2)
    pub Void DoWork(ComputeCommandList commandList, CascadeShadowsContext cascadeShadowsContext, DirectionalLightShadowResources shadowResources, Vector2I screenResolution)
CloudShadowJob   (fields: 2)
    int Void DoWork(DirectCommandList commandList)
CloudWeatherMapJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList)
CubeTextureMipMapGenerationJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList, Int32 faceIndex, RenderTargetCubeTexture finalTexture, RenderTargetCubeTexture transferTexture)
DirectionalLightJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, ITexture2DView shadowRtView, DirectionalLightShadowResources shadowResources, IRenderTargetView rtView)
DirectionalLightShadowJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList, DirectionalLightShadowResources shadowResources, ResizableRenderTargetTexture rtView)
IndirectPlanetEnvironmentJob   (fields: 6)
    pub Void DoWork(DirectCommandList commandList, TransientConstantBuffer cameraSettingsBuffer, IRenderTargetView environmentProbeCloseTarget, IRenderTargetView environmentProbeFarTarget, ITexture2DView environmentProbeDepthTexture, RenderViewSlim& view)
IRCacheDebugJob   (fields: 3)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView lBuffer)
IRCachePrepareJob   (fields: 4)
    pub Void DoWork(ComputeCommandList commandList)
IRCacheSumJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList)
IRCacheTraceJob   (fields: 2)
    pub Void DoWork(ComputeCommandList commandList)
LegacyRaytraceGIJob   (fields: 12)
    pub Void DoWork(DirectCommandList commandList, IRWTexture2DView diffuseGiBuffer, IRWTexture2DView raytracedReflectionsBuffer, ITexture2DView localLightDiffuse, ITexture2DView exposure, ITextureCubeView skyboxIBL)
LocalLightsJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView rtView, IRenderTargetView rtViewDiffuseOnly, ClusteringContext clusteringResult, OutputGeometryBufferContext outputGeometryBuffers)
MipMapPreFilterJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList, RenderTargetCubeTexture sourceTexture, RenderTargetCubeTexture targetTexture, Int32 faceIndex, SampleQuality sampleCount)
    pub Void DoWork(DirectCommandList commandList, IManagedCubeTexture sourceTexture, RenderTargetCubeTexture targetTexture, Int32 faceIndex, SampleQuality sampleCount)
RaytraceGIJob   (fields: 9)
    pub Void DoWork(DirectCommandList commandList, IRWTexture2DView diffuseGIBuffer, IRWTexture2DView specularGIBuffer, ITexture2DView localLightDiffuse, ITexture2DView exposure, RTGIContext context)
SkyboxMotionVectorsJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, IRenderTargetView rtView)
CullingJob   (fields: 7)
    int Void DoWork(ComputeCommandList commandList, RenderViewSlim& renderView, PassLODSettings lodSettings, CullingContext cullingContext, OutputGeometryBufferContext outputBuffers, VisibilityListBufferContext visibilityListBufferContext, OcclusionContext occlusionContext, Boolean isFirstPass, Nullable`1& posViewToNegViewProj, Nullable`1 baseRenderView, Int32 rootEntityId, CharacterCullingBehavior characterCullingBehavior, Int32 cascadeIndex)
DecalSortingPassJob   (fields: 19)
    pub Void DoWork(DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers, ClusteringContext clusteringResult)
DeferredTexturingJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList, GeometryContext result, OutputGeometryBufferContext outputGeometryBuffers, Nullable`1 fsrMasks)
DepthPassJob   (fields: 0)
    pub Void DoWork(DirectCommandList commandList, TrackedCameraSettings& view, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IDepthStencilView depthStencil, Boolean clearRenderTargets, DepthJobType depthJobType, Boolean allowTessellation, Boolean isFarCascade)
DepthPrePassJob   (fields: 0)
    pub Void DoWork(DirectCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, Boolean clearRenderTargets, Boolean allowTessellation)
GBufferDecalPassJob   (fields: 2)
    pub Void DoWork(DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers, ClusteringContext clusteringResult)
GBufferPassJob   (fields: 0)
    pub Void DoWork(DirectCommandList commandList, GeometryContext result, OutputGeometryBufferContext outputGeometryBuffers, Boolean clearRenderTargets, Nullable`1 fsrMasks)
HologramPassJob   (fields: 8)
    pub Boolean DoWork(DirectCommandList commandList, GeometryContext geometryContextFirstPass, OutputGeometryBufferContext geometryBuffers, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, IRenderTargetView oitAccumBuffer, IRenderTargetView oitCoverageBuffer, T rtView, IRenderTargetView motionVectors, ITexture2DView exposure, Nullable`1 fsrMasks, GeometryContext geometryContextSecondPass)
IndirectEnvironmentPassJob   (fields: 4)
    pub Void DoWork(DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers, TransientConstantBuffer cameraSettingsBuffer, RenderViewSlim& view, GeometryContext result, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, IRenderTargetView rt, IDepthStencilView depthStencil, Boolean clearRenderTarget)
LocalLightSortingPassJob   (fields: 3)
    pub Void DoWork(DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers, ClusteringContext clusteringResult)
SurfelPassJob   (fields: 0)
    pub Void DoWork(DirectCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView dummyRT, Vector2I resolution, TransientConstantBuffer cameraSettings, SurfelBuffer surfelBuffer, TransientConstantBuffer surfelSetup)
TerrainBlendingJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers)
TopMostPassJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, GeometryContext& geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView rtView)
TransparentPassJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, GeometryContext geometryContext, OutputGeometryBufferContext outputGeometryBuffers, ClusteringContext clusteredEntities, DirectionalLightShadowResources shadowResources, IRenderTargetView oitAccumBuffer, IRenderTargetView oitCoverageBuffer, T rtView, IRenderTargetView motionVectors, ITexture2DView exposure, Nullable`1 fsrMasks, ITexture2DView depthHierarchy, Nullable`1 sssrBuffer, ITexture2DView waterDepthBuffer, ITexture2DView waterThicknessBuffer, IDepthStencilView depthBuffer, ITexture2DView occluderDepthBuffer)
UnlitPassJob   (fields: 1)
    pub Void DoWork(DirectCommandList commandList, GeometryContext& geometryContext, OutputGeometryBufferContext outputGeometryBuffers, IRenderTargetView rtView, ITexture2DView exposure)
```

## 24. The foreign-view passes, in full

### `Keen.VRage.Render12.Core.Systems.SceneDrawSystem.ExecuteEnvironmentProbeUpdate`

Locals: 25, instructions: 316

```
  ldarg.2        
  ldflda         Request.Render : Nullable`1
  call           Nullable`1.get_HasValue
  brfalse        IL_02d2: ldarg.2
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_Environment
  ldfld          EnvironmentSettings.ProbeSettings : EnvironmentProbeSettings
  ldfld          EnvironmentProbeSettings.EnableRenderBlocks : Boolean
  brfalse        IL_02d2: ldarg.2
  ldstr          EnvProbe_Render
  ldc.i4.0       
  ldc.i4         185
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.0        
  ldarg.1        
  ldstr          EnvProbe_Render
  callvirt       CopyCommandList.BeginBlock
  stloc.1        
  ldstr          Setup
  ldc.i4.0       
  ldc.i4         188
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.2        
  ldarg.1        
  ldstr          Setup
  callvirt       CopyCommandList.BeginBlock
  stloc.3        
  ldarg.2        
  ldflda         Request.Render : Nullable`1
  call           Nullable`1.get_Value
  stloc.s        V_4
  ldloc.s        V_4
  ldfld          Render.OutputCloseTexture : RenderTargetCubeTexture
  ldloc.s        V_4
  ldfld          Render.FaceIndex : Int32
  callvirt       RenderTargetCubeTexture.GetRenderTargetFace
  stloc.s        V_5
  ldloc.s        V_4
  ldfld          Render.OutputFarTexture : RenderTargetCubeTexture
  ldloc.s        V_4
  ldfld          Render.FaceIndex : Int32
  callvirt       RenderTargetCubeTexture.GetRenderTargetFace
  stloc.s        V_6
  ldarg.1        
  ldloc.s        V_6
  ldloca.s       V_10
  initobj        Nullable`1
  ldloc.s        V_10
  callvirt       DirectCommandList.ClearRenderTargetView
  ldloca.s       V_11
  initobj        TrackedCameraSettings
  ldloca.s       V_11
  ldloca.s       V_4
  ldflda         Render.View : RenderViewSlim
  call           RenderViewSlim.op_Implicit
  stfld          TrackedCameraSettings.Camera : CameraSettings
  ldloca.s       V_11
  ldloca.s       V_12
  initobj        ScreenSettings
  ldloca.s       V_12
  ldloc.s        V_5
  callvirt       IRenderTargetView.get_Resolution
  call           Vector2I.op_Implicit
  stfld          ScreenSettings.Resolution : Vector2
  ldloc.s        V_12
  stfld          TrackedCameraSettings.Screen : ScreenSettings
  ldloc.s        V_11
  stloc.s        V_7
  ldsfld         CoreSystems.BindableBuffers : BindableBufferManager
  ldstr          cameraSettingsBuffer
  ldloca.s       V_7
  callvirt       BindableBufferManager.CreateTransientConstantBuffer
  stloc.s        V_8
  ldsfld         CoreSystems.BindableTexturePool : BindableTexturePoolManager
  ldstr          EnvProbeDepthTexture
  ldsfld         DepthStencilFormat.HighQuality : DepthStencilFormat
  ldloc.s        V_4
  ldfld          Render.Resolution : Vector2I
  ldloca.s       V_13
  initobj        Nullable`1
  ldloc.s        V_13
  ldc.i4         128
  callvirt       BindableTexturePoolManager.BorrowResizableDepthStencilTexture
  stloc.s        V_9
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  callvirt       OutputGeometryBufferContext.Borrow
  ldarg.1        
  ldloca.s       V_9
  call           Borrowed`1.get_Resource
  callvirt       ResizableDepthStencilTexture.get_DepthStencilReadWrite
  ldc.i4.3       
  callvirt       DirectCommandList.ClearDepthStencilView
  ldarg.0        
  ldfld          SceneDrawSystem._indirectCullingJob : CullingJob
  ldarg.1        
  ldloca.s       V_4
  ldflda         Render.View : RenderViewSlim
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_LOD
  ldfld          LODSettings.EnvironmentProbe : PassLODSettings
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_EnvProbeCulling
  ldloc.s        V_4
  ldfld          Render.FaceIndex : Int32
  ldelem.ref     
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldnull         
  ldnull         
  ldloca.s       V_14
  initobj        Nullable`1
  ldloca.s       V_15
  initobj        Nullable`1
  ldloc.s        V_15
  ldc.i4.m1      
  ldc.i4.0       
  ldc.i4.m1      
  callvirt       CullingJob.DoCullingFirstPass
  ldarg.0        
  ldfld          SceneDrawSystem._clusterJob : ClusteringJob
  ldarg.1        
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_EnvProbeCulling
  ldloc.s        V_4
  ldfld          Render.FaceIndex : Int32
  ldelem.ref     
  callvirt       CullingContext.get_EntityProxies
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_EnvProbeClustering
  ldloca.s       V_4
  ldflda         Render.Resolution : Vector2I
  ldloc.s        V_4
  ldfld          Render.FarPlane : Single
  callvirt       ClusteringJob.DoWork
  ldarg.0        
  ldfld          SceneDrawSystem._indirectEnvironmentPass : IndirectEnvironmentPassJob
  ldarg.1        
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldloc.s        V_8
  ldloca.s       V_4
  ldflda         Render.View : RenderViewSlim
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_EnvProbeCulling
  ldloc.s        V_4
  ldfld          Render.FaceIndex : Int32
  ldelem.ref     
  callvirt       CullingContext.get_FirstPass
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_EnvProbeClustering
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_DirectionalLightShadowResources
  ldloc.s        V_5
  ldloca.s       V_9
  call           Borrowed`1.get_Resource
  callvirt       ResizableDepthStencilTexture.get_DepthStencilReadWrite
  ldc.i4.1       
  callvirt       IndirectEnvironmentPassJob.DoWork
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  callvirt       OutputGeometryBufferContext.Return
  ldarg.0        
  call           SceneDrawSystem.get_Is3DMapEnabled
  brtrue.s       IL_028a: ldsfld Keen.VRage.Render12.Resources.BindableTextures.BindableTexturePoolManager Keen.VRage.Render12.…
  ldarg.0        
  ldfld          SceneDrawSystem._indirectPlanetEnvironmentJob : IndirectPlanetEnvironmentJob
  ldarg.1        
  ldloc.s        V_8
  ldloc.s        V_5
  ldloc.s        V_6
  ldloca.s       V_9
  call           Borrowed`1.get_Resource
  callvirt       ResizableDepthStencilTexture.get_DepthTexture
  ldloca.s       V_4
  ldflda         Render.View : RenderViewSlim
  callvirt       IndirectPlanetEnvironmentJob.DoWork
  ldsfld         CoreSystems.BindableTexturePool : BindableTexturePoolManager
  ldloc.s        V_9
  callvirt       BindableTexturePoolManager.Return
  leave.s        IL_02d2: ldarg.2
  ldloca.s       V_8
  constrained.   TransientConstantBuffer
  callvirt       IDisposable.Dispose
  ldloca.s       V_3
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_2
  call           ProfilingScope.Dispose
  ldloca.s       V_1
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_0
  call           ProfilingScope.Dispose
  ldarg.2        
  ldflda         Request.MipMapGeneration : Nullable`1
  call           Nullable`1.get_HasValue
  brfalse.s      IL_0349: ldarg.2
  ldstr          EnvProbe_MipMapGeneration
  ldc.i4.0       
  ldc.i4         266
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.s        V_16
  ldarg.1        
  ldstr          EnvProbe_MipMapGeneration
  callvirt       CopyCommandList.BeginBlock
  stloc.s        V_17
  ldarg.2        
  ldflda         Request.MipMapGeneration : Nullable`1
  call           Nullable`1.get_Value
  stloc.s        V_18
  ldarg.0        
  ldfld          SceneDrawSystem._cubeTextureMipMapGenerationJob : CubeTextureMipMapGenerationJob
  ldarg.1        
  ldloc.s        V_18
  ldfld          MipMapGeneration.FaceIndex : Int32
  ldloc.s        V_18
  ldfld          MipMapGeneration.InputTexture : RenderTargetCubeTexture
  ldloc.s        V_18
  ldfld          MipMapGeneration.OutputTexture : RenderTargetCubeTexture
  callvirt       CubeTextureMipMapGenerationJob.DoWork
  leave.s        IL_0349: ldarg.2
  ldloca.s       V_17
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_16
  call           ProfilingScope.Dispose
  ldarg.2        
  ldflda         Request.PreFiltering : Nullable`1
  call           Nullable`1.get_HasValue
  brfalse.s      IL_03c2: ldarg.2
  ldstr          EnvProbe_PreFiltering
  ldc.i4.0       
  ldc.i4         279
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.s        V_19
  ldarg.1        
  ldstr          EnvProbe_PreFiltering
  callvirt       CopyCommandList.BeginBlock
  stloc.s        V_20
  ldarg.2        
  ldflda         Request.PreFiltering : Nullable`1
  call           Nullable`1.get_Value
  stloc.s        V_21
  ldarg.0        
  ldfld          SceneDrawSystem._mipMapPreFilterJob : MipMapPreFilterJob
  ldarg.1        
  ldloc.s        V_21
  ldfld          PreFiltering.InputTexture : RenderTargetCubeTexture
  ldloc.s        V_21
  ldfld          PreFiltering.OutputTexture : RenderTargetCubeTexture
  ldloc.s        V_21
  ldfld          PreFiltering.FaceIndex : Int32
  ldc.i4.s       64
  callvirt       MipMapPreFilterJob.DoWork
  leave.s        IL_03c2: ldarg.2
  ldloca.s       V_20
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_19
  call           ProfilingScope.Dispose
  ldarg.2        
  ldflda         Request.Blending : Nullable`1
  call           Nullable`1.get_HasValue
  brfalse.s      IL_0440: ret
  ldstr          EnvProbe_Blending
  ldc.i4.0       
  ldc.i4         293
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.s        V_22
  ldarg.1        
  ldstr          EnvProbe_Blending
  callvirt       CopyCommandList.BeginBlock
  stloc.s        V_23
  ldarg.2        
  ldflda         Request.Blending : Nullable`1
  call           Nullable`1.get_Value
  stloc.s        V_24
  ldarg.0        
  ldfld          SceneDrawSystem._environmentProbeBlending : EnvironmentProbeBlending
  ldarg.1        
  ldloc.s        V_24
  ldfld          Blending.FaceIndex : Int32
  ldloc.s        V_24
  ldfld          Blending.BlendWeight : Single
  ldloc.s        V_24
  ldfld          Blending.InputTexture : RenderTargetCubeTexture
  ldloc.s        V_24
  ldfld          Blending.OutputTexture : RenderTargetCubeTexture
  callvirt       EnvironmentProbeBlending.DoWork_BlendWeight
  leave.s        IL_0440: ret
  ldloca.s       V_23
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_22
  call           ProfilingScope.Dispose
```

### `Keen.VRage.Render12.Core.Systems.SceneDrawSystem.RenderShadowCascades`

Locals: 17, instructions: 277

```
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_HiZContext
  ldarg.1        
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_HiZContext
  callvirt       HiZContext.get_CascadeResolution
  callvirt       HiZContext.Borrow
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  ldflda         DrawContextManager.CascadesToUpdate : Buffer`1
  call           Buffer`1.GetEnumerator
  stloc.0        
  br             IL_0356: ldloca.s V_0
  ldloca.s       V_0
  call           Enumerator.get_Current
  ldobj          CascadeUpdateInfo
  stloc.1        
  ldstr          ShadowCascade
  ldc.i4.0       
  ldc.i4         524
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.2        
  ldarg.1        
  ldstr          ShadowCascade
  callvirt       CopyCommandList.BeginBlock
  stloc.3        
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_LOD
  ldfld          LODSettings.CascadeDepths : PassLODSettings[]
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldelem.any     PassLODSettings
  stloc.s        V_4
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_Shadow
  ldfld          ShadowSettings.DirectionalLight : DirectionalLightSettings
  ldfld          DirectionalLightSettings.EnableTessellation : Boolean
  brfalse.s      IL_00ab: ldc.i4.0
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  brfalse.s      IL_00a8: ldc.i4.1
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldc.i4.1       
  br.s           IL_00ac: stloc.s V_5
  ldc.i4.1       
  br.s           IL_00ac: stloc.s V_5
  ldc.i4.0       
  stloc.s        V_5
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_Shadow
  ldfld          ShadowSettings.DirectionalLight : DirectionalLightSettings
  ldfld          DirectionalLightSettings.CascadesCount : Int32
  ldc.i4.2       
  ldc.i4.0       
  stloc.s        V_6
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_CascadeOcclusion
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldelem.ref     
  stloc.s        V_7
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_CascadeVisibilityLists
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldelem.ref     
  stloc.s        V_8
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  callvirt       OutputGeometryBufferContext.Borrow
  ldloc.s        V_8
  callvirt       VisibilityListBufferContext.Borrow
  ldstr          CascadeCulling[FirstPass]
  ldc.i4.0       
  ldc.i4         539
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.s        V_9
  ldarg.1        
  ldstr          CascadeCulling[FirstPass]
  callvirt       CopyCommandList.BeginBlock
  stloc.s        V_10
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_CascadeCulling
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldelem.ref     
  stloc.s        V_11
  ldarg.0        
  ldfld          SceneDrawSystem._cascadeCullingJob : CullingJob
  ldarg.1        
  ldloca.s       V_1
  ldflda         CascadeUpdateInfo.RenderViewSlim : RenderViewSlim
  ldloc.s        V_4
  ldloc.s        V_11
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldloc.s        V_8
  ldloc.s        V_7
  ldloca.s       V_1
  ldflda         CascadeUpdateInfo.PositiveViewToNegativeViewProj : Nullable`1
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  stloc.s        V_13
  ldloca.s       V_14
  initobj        Nullable`1
  ldloc.s        V_14
  ldc.i4.m1      
  ldc.i4.3       
  ldloc.s        V_13
  callvirt       CullingJob.DoCullingFirstPass
  ldloca.s       V_15
  initobj        TrackedCameraSettings
  ldloca.s       V_15
  ldloc.1        
  ldfld          CascadeUpdateInfo.RenderCameraSettings : CameraSettings
  stfld          TrackedCameraSettings.Camera : CameraSettings
  ldloca.s       V_15
  ldloca.s       V_16
  initobj        ScreenSettings
  ldloca.s       V_16
  ldloc.1        
  ldfld          CascadeUpdateInfo.DepthTexture : DepthStencilTexture
  callvirt       DepthStencilTexture.get_Resolution
  call           Vector2I.op_Implicit
  stfld          ScreenSettings.Resolution : Vector2
  ldloc.s        V_16
  stfld          TrackedCameraSettings.Screen : ScreenSettings
  ldloc.s        V_15
  stloc.s        V_12
  ldloca.s       V_10
  ldstr          DepthPassSingles[FirstPass]
  call           BlockScope.BeginNextBlock
  ldarg.0        
  ldfld          SceneDrawSystem._shadowsDepthPass : DepthPassJob
  ldarg.1        
  ldloca.s       V_12
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_CascadeCulling
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldelem.ref     
  callvirt       CullingContext.get_FirstPass
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldloc.1        
  ldfld          CascadeUpdateInfo.DepthTexture : DepthStencilTexture
  callvirt       DepthStencilTexture.get_DepthStencilReadWrite
  ldc.i4.1       
  ldc.i4.1       
  ldloc.s        V_5
  ldloc.s        V_6
  callvirt       DepthPassJob.DoWork
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_HZBO
  ldfld          HZBOSettings.Enabled : Boolean
  brfalse        IL_0312: ldsfld Keen.VRage.Render12.Core.Systems.DrawContextManager Keen.VRage.Render12.Core.CoreSystems::Draw…
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_HZBO
  ldfld          HZBOSettings.CascadesEnabled : Boolean
  brfalse        IL_0312: ldsfld Keen.VRage.Render12.Core.Systems.DrawContextManager Keen.VRage.Render12.Core.CoreSystems::Draw…
  ldloca.s       V_10
  ldstr          Cascades HiZBuffer
  call           BlockScope.BeginNextBlock
  ldarg.0        
  ldarg.1        
  ldloc.s        V_7
  ldloc.1        
  ldfld          CascadeUpdateInfo.DepthTexture : DepthStencilTexture
  callvirt       DepthStencilTexture.get_DepthTexture
  call           SceneDrawSystem.BuildHiZBuffer
  ldloca.s       V_10
  ldstr          CascadeCulling[SecondPass]
  call           BlockScope.BeginNextBlock
  ldarg.0        
  ldfld          SceneDrawSystem._cascadeCullingJob : CullingJob
  ldarg.1        
  ldloca.s       V_1
  ldflda         CascadeUpdateInfo.RenderViewSlim : RenderViewSlim
  ldloc.s        V_4
  ldloc.s        V_11
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldloc.s        V_8
  ldloc.s        V_7
  ldloca.s       V_1
  ldflda         CascadeUpdateInfo.PositiveViewToNegativeViewProj : Nullable`1
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  stloc.s        V_13
  ldloca.s       V_14
  initobj        Nullable`1
  ldloc.s        V_14
  ldc.i4.m1      
  ldc.i4.3       
  ldloc.s        V_13
  callvirt       CullingJob.DoCullingSecondPass
  ldloca.s       V_10
  ldstr          DepthPassSingles[SecondPass]
  call           BlockScope.BeginNextBlock
  ldarg.0        
  ldfld          SceneDrawSystem._shadowsDepthPass : DepthPassJob
  ldarg.1        
  ldloca.s       V_12
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_CascadeCulling
  ldloc.1        
  ldfld          CascadeUpdateInfo.CascadeIndex : Int32
  ldelem.ref     
  callvirt       CullingContext.get_SecondPass
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldloc.1        
  ldfld          CascadeUpdateInfo.DepthTexture : DepthStencilTexture
  callvirt       DepthStencilTexture.get_DepthStencilReadWrite
  ldc.i4.0       
  ldc.i4.1       
  ldloc.s        V_5
  ldc.i4.0       
  callvirt       DepthPassJob.DoWork
  ldloca.s       V_10
  ldstr          Cascades HiZBuffer
  call           BlockScope.BeginNextBlock
  ldarg.0        
  ldarg.1        
  ldloc.s        V_7
  ldloc.1        
  ldfld          CascadeUpdateInfo.DepthTexture : DepthStencilTexture
  callvirt       DepthStencilTexture.get_DepthTexture
  call           SceneDrawSystem.BuildHiZBuffer
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  callvirt       OutputGeometryBufferContext.Return
  ldloc.s        V_8
  callvirt       VisibilityListBufferContext.Return
  leave.s        IL_0356: ldloca.s V_0
  ldloca.s       V_10
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_9
  call           ProfilingScope.Dispose
  ldloca.s       V_3
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_2
  call           ProfilingScope.Dispose
  ldloca.s       V_0
  call           Enumerator.MoveNext
  brtrue         IL_0034: ldloca.s V_0
  leave.s        IL_036c: ldsfld Keen.VRage.Render12.Core.Systems.SettingsManager Keen.VRage.Render12.Core.CoreSystems::Setting…
  ldloca.s       V_0
  call           Enumerator.Dispose
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_Shadow
  ldfld          ShadowSettings.DirectionalLight : DirectionalLightSettings
  ldfld          DirectionalLightSettings.EnableCascadeMerging : Boolean
  brfalse.s      IL_038e: ldsfld Keen.VRage.Render12.Core.Systems.DrawContextManager Keen.VRage.Render12.Core.CoreSystems::Draw…
  ldarg.0        
  ldfld          SceneDrawSystem._cascadeShadowsMergeJob : CascadeShadowsMergeJob
  ldarg.1        
  callvirt       CascadeShadowsMergeJob.DoWork
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_HiZContext
  callvirt       HiZContext.Return
```

### `Keen.VRage.Render12.Core.Systems.SceneDrawSystem.DrawUnlit`

Locals: 3, instructions: 62

```
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_Overrides
  ldfld          OverridesSettings.TopMostPass : Boolean
  brtrue.s       IL_0012: ldstr "UnlitPass"
  ldstr          UnlitPass
  ldc.i4.0       
  ldc.i4         700
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\Core\Systems\Scene…
  call           Profiler.Begin
  stloc.0        
  ldarg.1        
  ldstr          UnlitPass
  callvirt       CopyCommandList.BeginBlock
  stloc.1        
  ldarg.0        
  ldfld          SceneDrawSystem._unlitPass : UnlitPassJob
  ldarg.1        
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainViewCulling
  callvirt       CullingContext.get_FirstPass
  stloc.2        
  ldloca.s       V_2
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldarg.2        
  ldarg.0        
  ldfld          SceneDrawSystem._eyeAdaptationJob : EyeAdaptationJob
  callvirt       EyeAdaptationJob.get_Exposure
  callvirt       UnlitPassJob.DoWork
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_HZBO
  ldfld          HZBOSettings.Enabled : Boolean
  brfalse.s      IL_00be: leave.s IL_00d6
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_HZBO
  ldfld          HZBOSettings.MainViewEnabled : Boolean
  brfalse.s      IL_00be: leave.s IL_00d6
  ldarg.0        
  ldfld          SceneDrawSystem._unlitPass : UnlitPassJob
  ldarg.1        
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainViewCulling
  callvirt       CullingContext.get_SecondPass
  stloc.2        
  ldloca.s       V_2
  ldsfld         CoreSystems.DrawContexts : DrawContextManager
  callvirt       DrawContextManager.get_MainOutputGeometryBuffers
  ldarg.2        
  ldarg.0        
  ldfld          SceneDrawSystem._eyeAdaptationJob : EyeAdaptationJob
  callvirt       EyeAdaptationJob.get_Exposure
  callvirt       UnlitPassJob.DoWork
  leave.s        IL_00d6: ret
  ldloca.s       V_1
  constrained.   BlockScope
  callvirt       IDisposable.Dispose
  ldloca.s       V_0
  call           ProfilingScope.Dispose
```

### `Keen.VRage.Render12.LightingStage.EnvironmentProbeManager.PrepareProbes`

Locals: 18, instructions: 388

```
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_Environment
  ldfld          EnvironmentSettings.ProbeSettings : EnvironmentProbeSettings
  stloc.0        
  ldloc.0        
  ldfld          EnvironmentProbeSettings.Enable : Boolean
  brtrue.s       IL_003c: ldc.i4.0
  ldarg.0        
  ldflda         EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldfld          EnvironmentProbeSettings.Enable : Boolean
  brfalse.s      IL_002c: ldarg.0
  ldarg.0        
  ldloc.0        
  stfld          EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldarg.0        
  call           EnvironmentProbeManager.DisposeTextures
  ldloca.s       V_3
  initobj        Buffer`1
  ldloc.3        
  ldc.i4.0       
  stloc.1        
  ldarg.0        
  ldloca.s       V_0
  call           EnvironmentProbeManager.NeedsReprocess
  brtrue.s       IL_0065: ldarg.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._forceReprocess : Boolean
  brtrue.s       IL_0065: ldarg.0
  ldsfld         CoreSystems.Settings : SettingsManager
  callvirt       SettingsManager.get_RenderView
  stloc.s        V_4
  ldloca.s       V_4
  call           RenderView.get_LastUpdateWasSmooth
  brtrue.s       IL_0078: ldarg.0
  ldarg.0        
  ldloca.s       V_0
  call           EnvironmentProbeManager.RecreateProbes
  ldc.i4.1       
  stloc.1        
  ldarg.0        
  ldc.i4.0       
  stfld          EnvironmentProbeManager._forceReprocess : Boolean
  br.s           IL_008d: ldloc.0
  ldarg.0        
  ldflda         EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldloc.0        
  call           EnvironmentProbeSettings.Equals
  brtrue.s       IL_008d: ldloc.0
  ldarg.0        
  ldloc.0        
  stfld          EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldloc.0        
  ldfld          EnvironmentProbeSettings.EnableFullUpdate : Boolean
  ldloc.1        
  brfalse        IL_0226: ldarg.0
  ldarg.0        
  ldc.i4.m1      
  stfld          EnvironmentProbeManager._state : Int32
  ldarg.0        
  call           EnvironmentProbeManager.UpdateLocalLightAmbient
  ldloca.s       V_5
  ldc.i4.s       24
  ldc.i4.2       
  ldstr          PrepareProbes
  call           Buffer`1..ctor
  ldc.i4.0       
  stloc.s        V_6
  br             IL_015d: ldloc.s V_6
  ldloca.s       V_5
  ldloca.s       V_7
  initobj        Request
  ldloca.s       V_7
  ldarg.0        
  ldflda         EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldfld          EnvironmentProbeSettings.EnableRenderBlocks : Boolean
  brtrue.s       IL_00e3: ldarg.0
  ldloca.s       V_8
  initobj        Render
  ldloc.s        V_8
  br.s           IL_00f7: newobj System.Void System.Nullable`1<Keen.VRage.Render12.LightingStage.EnvironmentProbeManager/Render…
  ldarg.0        
  ldloc.s        V_6
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureA : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureA : RenderTargetCubeTexture
  newobj         Render..ctor
  newobj         Nullable`1..ctor
  stfld          Request.Render : Nullable`1
  ldloca.s       V_7
  ldloc.s        V_6
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureA : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureB : RenderTargetCubeTexture
  newobj         MipMapGeneration..ctor
  newobj         Nullable`1..ctor
  stfld          Request.MipMapGeneration : Nullable`1
  ldloc.s        V_7
  call           Buffer`1.Add
  ldloca.s       V_5
  ldloca.s       V_7
  initobj        Request
  ldloca.s       V_7
  ldloc.s        V_6
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureA : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureB : RenderTargetCubeTexture
  newobj         MipMapGeneration..ctor
  newobj         Nullable`1..ctor
  stfld          Request.MipMapGeneration : Nullable`1
  ldloc.s        V_7
  call           Buffer`1.Add
  ldloc.s        V_6
  ldc.i4.1       
  stloc.s        V_6
  ldloc.s        V_6
  ldc.i4.6       
  blt            IL_00be: ldloca.s V_5
  ldc.i4.0       
  stloc.s        V_9
  br             IL_021b: ldloc.s V_9
  ldloca.s       V_5
  ldloca.s       V_7
  initobj        Request
  ldloca.s       V_7
  ldloc.s        V_9
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureB : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeBlendTexture : RenderTargetCubeTexture
  newobj         PreFiltering..ctor
  newobj         Nullable`1..ctor
  stfld          Request.PreFiltering : Nullable`1
  ldloca.s       V_7
  ldloc.s        V_9
  ldc.r4         1
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeBlendTexture : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeFinalTexture : RenderTargetCubeTexture
  newobj         Blending..ctor
  newobj         Nullable`1..ctor
  stfld          Request.Blending : Nullable`1
  ldloc.s        V_7
  call           Buffer`1.Add
  ldloca.s       V_5
  ldloca.s       V_7
  initobj        Request
  ldloca.s       V_7
  ldloc.s        V_9
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureB : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farBlendTexture : RenderTargetCubeTexture
  newobj         PreFiltering..ctor
  newobj         Nullable`1..ctor
  stfld          Request.PreFiltering : Nullable`1
  ldloca.s       V_7
  ldloc.s        V_9
  ldc.r4         1
  ldarg.0        
  ldfld          EnvironmentProbeManager._farBlendTexture : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farFinalTexture : RenderTargetCubeTexture
  newobj         Blending..ctor
  newobj         Nullable`1..ctor
  stfld          Request.Blending : Nullable`1
  ldloc.s        V_7
  call           Buffer`1.Add
  ldloc.s        V_9
  ldc.i4.1       
  stloc.s        V_9
  ldloc.s        V_9
  ldc.i4.6       
  blt            IL_016d: ldloca.s V_5
  ldloc.s        V_5
  ldarg.0        
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.1       
  stfld          EnvironmentProbeManager._state : Int32
  ldloca.s       V_2
  ldc.i4.s       12
  ldc.i4.2       
  ldstr          PrepareProbes
  call           Buffer`1..ctor
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  brtrue.s       IL_0261: ldarg.0
  ldarg.0        
  ldsfld         CoreSystems.Time : Time
  callvirt       Time.get_FrameTime
  stfld          EnvironmentProbeManager._startedUpdateTime : TimeSpan
  ldarg.0        
  call           EnvironmentProbeManager.UpdateLocalLightAmbient
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.6       
  blt.s          IL_02b7: ldarg.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       12
  bge.s          IL_02b7: ldarg.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.6       
  stloc.s        V_10
  ldarg.0        
  ldflda         EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldfld          EnvironmentProbeSettings.EnableRenderBlocks : Boolean
  brfalse        IL_0402: ldc.i4.0
  ldloca.s       V_2
  ldarg.0        
  ldloc.s        V_10
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureA : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureA : RenderTargetCubeTexture
  newobj         Render..ctor
  stloc.s        V_8
  ldloca.s       V_8
  call           Request.op_Implicit
  call           Buffer`1.Add
  br             IL_0402: ldc.i4.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       12
  bne.un.s       IL_02f9: ldarg.0
  ldc.i4.0       
  stloc.s        V_11
  br.s           IL_02ef: ldloc.s V_11
  ldloca.s       V_2
  ldloc.s        V_11
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureA : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureB : RenderTargetCubeTexture
  newobj         MipMapGeneration..ctor
  stloc.s        V_12
  ldloca.s       V_12
  call           Request.op_Implicit
  call           Buffer`1.Add
  ldloc.s        V_11
  ldc.i4.1       
  stloc.s        V_11
  ldloc.s        V_11
  ldc.i4.6       
  blt.s          IL_02c6: ldloca.s V_2
  br             IL_0402: ldc.i4.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       13
  bne.un.s       IL_033b: ldarg.0
  ldc.i4.0       
  stloc.s        V_13
  br.s           IL_0331: ldloc.s V_13
  ldloca.s       V_2
  ldloc.s        V_13
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureA : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureB : RenderTargetCubeTexture
  newobj         MipMapGeneration..ctor
  stloc.s        V_12
  ldloca.s       V_12
  call           Request.op_Implicit
  call           Buffer`1.Add
  ldloc.s        V_13
  ldc.i4.1       
  stloc.s        V_13
  ldloc.s        V_13
  ldc.i4.6       
  blt.s          IL_0308: ldloca.s V_2
  br             IL_0402: ldc.i4.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       14
  blt.s          IL_03a2: ldarg.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       20
  bge.s          IL_03a2: ldarg.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       14
  stloc.s        V_14
  ldloca.s       V_2
  ldloc.s        V_14
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeWorkTextureB : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeBlendTexture : RenderTargetCubeTexture
  newobj         PreFiltering..ctor
  stloc.s        V_15
  ldloca.s       V_15
  call           Request.op_Implicit
  call           Buffer`1.Add
  ldloca.s       V_2
  ldloc.s        V_14
  ldarg.0        
  ldfld          EnvironmentProbeManager._farWorkTextureB : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farBlendTexture : RenderTargetCubeTexture
  newobj         PreFiltering..ctor
  stloc.s        V_15
  ldloca.s       V_15
  call           Request.op_Implicit
  call           Buffer`1.Add
  br.s           IL_0402: ldc.i4.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.s       20
  blt.s          IL_0402: ldc.i4.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._startedUpdateTime : TimeSpan
  ldarg.0        
  ldflda         EnvironmentProbeManager._lastSettings : EnvironmentProbeSettings
  ldfld          EnvironmentProbeSettings.TimeOut : Single
  call           TimeSpan.FromSeconds
  call           TimeSpan.op_Addition
  ldsfld         CoreSystems.Time : Time
  callvirt       Time.get_FrameTime
  call           TimeSpan.op_LessThanOrEqual
  brfalse.s      IL_0402: ldc.i4.0
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4         128
  ldnull         
  ldstr          _state < MAX_STATE_COUNT
  ldstr          C:\BuildAgent\work\e958cd452eaeb7c\KeenSWH\Stable_VS2.3\VRage\Sources\Render\VRage.Render12\LightingStage\Envi…
  ldc.i4         174
  call           Assert.True
  ldarg.0        
  ldc.i4.m1      
  stfld          EnvironmentProbeManager._state : Int32
  ldc.i4.0       
  stloc.s        V_16
  br.s           IL_045b: ldloc.s V_16
  ldloca.s       V_2
  ldarg.0        
  ldfld          EnvironmentProbeManager._state : Int32
  ldc.i4.2       
  brfalse.s      IL_042e: ldloc.s V_16
  ldloc.s        V_16
  ldarg.0        
  call           EnvironmentProbeManager.GetBlendWeight
  ldarg.0        
  ldfld          EnvironmentProbeManager._farBlendTexture : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._farFinalTexture : RenderTargetCubeTexture
  newobj         Blending..ctor
  br.s           IL_0447: stloc.s V_17
  ldloc.s        V_16
  ldarg.0        
  call           EnvironmentProbeManager.GetBlendWeight
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeBlendTexture : RenderTargetCubeTexture
  ldarg.0        
  ldfld          EnvironmentProbeManager._closeFinalTexture : RenderTargetCubeTexture
  newobj         Blending..ctor
  stloc.s        V_17
  ldloca.s       V_17
  call           Request.op_Implicit
  call           Buffer`1.Add
  ldloc.s        V_16
  ldc.i4.1       
  stloc.s        V_16
  ldloc.s        V_16
  ldc.i4.6       
  blt.s          IL_0407: ldloca.s V_2
  ldloc.2        
```

## 25. SettingsManager.SetCameraParameters

### `SettingsManager.SetCameraParameters(WorldTransform& cameraTransform, Single fov, Single nearPlane, Single farPlane, Single veryFarPlane, Vector2 projectionOffset, Boolean smooth, Boolean orthographic)`

```
  call  SettingsManager.get_HZBO
  call  SettingsManager.get_HZBO
  STORE SettingsManager._freezedRenderView
  STORE SettingsManager._isViewFreezed
  call  WorldTransform.op_Implicit
  call  RenderView.SetCameraParameters
  call  CoreSystems.UpdateDebugDrawRoot
```

### Callers

```
Keen.VRage.Render12.Core.Systems.SettingsManager.SetCameraParameters
Keen.VRage.Render12.Core.Systems.SettingsManager/RenderSettingsComponent.Keen.VRage.Render12.Core.Contracts.IRenderSettings.SetCameraParameters
Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent.UpdateRenderSettingsInternal
```

## 26. Which CoreSystems globals get written, and by whom

A global only written by `CoreSystems.Initialize` is startup state and can
be ignored. Anything written elsewhere is live state a second pass must
account for.

```
<AreTasksCancelled>k__BackingField <- CoreSystems.set_AreTasksCancelled, CoreSystems..cctor
<EarlyTaskExit>k__BackingField     <- CoreSystems.set_EarlyTaskExit, CoreSystems..cctor
<IsInited>k__BackingField          <- CoreSystems.set_IsInited
<IsRecompilationSucceeded>k__BackingField <- CoreSystems.set_IsRecompilationSucceeded, CoreSystems..cctor
<IsUnderutilizationDetected>k__BackingField <- CoreSystems.set_IsUnderutilizationDetected, CoreSystems..cctor
<RenderLifetime>k__BackingField    <- CoreSystems..cctor
<RenderThread>k__BackingField      <- CoreSystems.set_RenderThread, CoreSystems..cctor
Adapters                           <- Render12EngineComponent.Init, CoreSystems..cctor
AllocLog                           <- CoreSystems.Initialize, CoreSystems..cctor
Atmospheres                        <- CoreSystems.Initialize, CoreSystems..cctor
BindableBuffers                    <- CoreSystems.Initialize, CoreSystems..cctor
BindableTexturePool                <- CoreSystems.Initialize, CoreSystems..cctor
BindableTextures                   <- CoreSystems.Initialize, CoreSystems..cctor
BlendStates                        <- CoreSystems.Initialize, CoreSystems..cctor
ClearingManager                    <- CoreSystems.Initialize, CoreSystems..cctor
Clouds                             <- CoreSystems.Initialize, CoreSystems..cctor
CloudsModifierAllocator            <- CoreSystems.Initialize, CoreSystems..cctor
CommandSignatures                  <- CoreSystems.Initialize, CoreSystems..cctor
CommonResources                    <- CoreSystems.Initialize, CoreSystems..cctor
ComputePSOs                        <- CoreSystems.Initialize, CoreSystems..cctor
CopyLDRJob                         <- CoreSystems.Initialize, CoreSystems..cctor
CrashGPUJob                        <- CoreSystems.Initialize, CoreSystems..cctor
CullCapacityTrackingManager        <- CoreSystems.Initialize, CoreSystems..cctor
D3DHeap                            <- CoreSystems.Initialize, CoreSystems..cctor
DataUploader                       <- CoreSystems.Initialize, CoreSystems..cctor
DebugPassJob                       <- CoreSystems.Initialize, CoreSystems..cctor
DebugReadback                      <- CoreSystems.Initialize, CoreSystems..cctor
Decals                             <- CoreSystems.Initialize, CoreSystems..cctor
DefinitionPostProcesses            <- CoreSystems.Initialize, CoreSystems..cctor
DepthStencilStates                 <- CoreSystems.Initialize, CoreSystems..cctor
DescriptorHeap                     <- CoreSystems.Initialize, CoreSystems..cctor
DeviceContext                      <- CoreSystems.Initialize, CoreSystems..cctor
DeviceWrap                         <- CoreSystems..cctor
DirectStorage                      <- CoreSystems.Initialize, CoreSystems..cctor
DistanceTagManager                 <- CoreSystems.Initialize, CoreSystems..cctor
DrawContexts                       <- CoreSystems.Initialize, CoreSystems..cctor
EnvironmentProbeManager            <- CoreSystems.Initialize, CoreSystems..cctor
FloraSystem                        <- CoreSystems.Initialize, CoreSystems..cctor
Fonts                              <- CoreSystems.InitializeSyncSystems, CoreSystems..cctor
FrameDispatcher                    <- CoreSystems.Initialize, CoreSystems..cctor
FramePacer                         <- CoreSystems.InitFramePacer, CoreSystems..cctor
FrameSpan                          <- CoreSystems.Initialize, CoreSystems..cctor
FrameUploadManager                 <- CoreSystems.Initialize, CoreSystems..cctor
GPUFrameManager                    <- CoreSystems.Initialize, CoreSystems..cctor
GPUProfiler                        <- CoreSystems.Initialize, CoreSystems..cctor
GPUResourcePool                    <- CoreSystems.Initialize, CoreSystems..cctor
GPUScene                           <- CoreSystems.Initialize, CoreSystems..cctor
GPUStats                           <- CoreSystems.Initialize, CoreSystems..cctor
GeometryPSOCache                   <- CoreSystems.Initialize, CoreSystems..cctor
GraphicsPSOs                       <- CoreSystems.Initialize, CoreSystems..cctor
HierarchicalContainer              <- CoreSystems.Initialize, CoreSystems..cctor
IBLs                               <- CoreSystems.Initialize, CoreSystems..cctor
IRCacheResources                   <- CoreSystems.Initialize, CoreSystems..cctor
ImpostorBakingManager              <- CoreSystems.Initialize, CoreSystems..cctor
ImpostorManager                    <- CoreSystems.Initialize, CoreSystems..cctor
InputLayouts                       <- CoreSystems.Initialize, CoreSystems..cctor
LoadingMonitor                     <- CoreSystems.InitializeSyncSystems, CoreSystems..cctor
LocalLights                        <- CoreSystems.Initialize, CoreSystems..cctor
Log                                <- CoreSystems..cctor
MainUISystem                       <- CoreSystems.Initialize, CoreSystems..cctor
ManagedROBuffers                   <- CoreSystems.Initialize, CoreSystems..cctor
ManagedRuntimeBuffers              <- CoreSystems.Initialize, CoreSystems..cctor
ManagedTexturePinManager           <- CoreSystems.Initialize, CoreSystems..cctor
ManagedTexturePrioritizer          <- CoreSystems.Initialize, CoreSystems..cctor
ManagedTextureStreaming            <- CoreSystems.Initialize, CoreSystems..cctor
ManagedTextures                    <- CoreSystems.Initialize, CoreSystems..cctor
MaterialRootSignatures             <- CoreSystems.Initialize, CoreSystems..cctor
Materials                          <- CoreSystems.Initialize, CoreSystems..cctor
MemoryHierarchy                    <- CoreSystems.Initialize, CoreSystems..cctor
MeshBuilderFactory                 <- CoreSystems.Initialize, CoreSystems..cctor
MeshEffectSystem                   <- CoreSystems.Initialize, CoreSystems..cctor
Messages                           <- CoreSystems..cctor
ModelManager                       <- CoreSystems.Initialize, CoreSystems..cctor
ModelUIB                           <- CoreSystems.Initialize, CoreSystems..cctor
ObjectPoolMonitor                  <- CoreSystems.Initialize, CoreSystems..cctor
OffscreenTarget                    <- CoreSystems.Initialize, CoreSystems..cctor
OffscreenUIRenderer                <- CoreSystems.Initialize, CoreSystems..cctor
ParallelBatchManager               <- CoreSystems.Initialize, CoreSystems..cctor
ParticleEffectManager              <- CoreSystems.Initialize, CoreSystems..cctor
ParticleSystem                     <- CoreSystems.Initialize, CoreSystems..cctor
PlanetEnvironments                 <- CoreSystems.Initialize, CoreSystems..cctor
QueryHeaps                         <- CoreSystems.Initialize, CoreSystems..cctor
RASBuffers                         <- CoreSystems.Initialize, CoreSystems..cctor
RWTexture2DTables                  <- CoreSystems.Initialize, CoreSystems..cctor
RasterizerStates                   <- CoreSystems.Initialize, CoreSystems..cctor
RayTracingBLASManager              <- CoreSystems.Initialize, CoreSystems..cctor
RayTracingPSOs                     <- CoreSystems.Initialize, CoreSystems..cctor
RayTracingScene                    <- CoreSystems.Initialize, CoreSystems..cctor
RaytraceGIResources                <- CoreSystems.Initialize, CoreSystems..cctor
RecordedActionSequenceReports      <- CoreSystems.Initialize, CoreSystems..cctor
RenderIds                          <- CoreSystems.Initialize, CoreSystems..cctor
ReplayedActionSequenceReports      <- CoreSystems.Initialize, CoreSystems..cctor
ResourceStateMonitor               <- CoreSystems.Initialize, CoreSystems..cctor
ResourceUploadSynchronizationManager <- CoreSystems.Initialize, CoreSystems..cctor
RootSignatures                     <- CoreSystems.Initialize, CoreSystems..cctor
RuntimeBufferEntities              <- CoreSystems.Initialize, CoreSystems..cctor
Samplers                           <- CoreSystems.Initialize, CoreSystems..cctor
Scene                              <- CoreSystems.InitializeSyncSystems, CoreSystems..cctor
SceneDrawSystem                    <- CoreSystems.Initialize, CoreSystems..cctor
ScreenBuffers                      <- CoreSystems.Initialize, CoreSystems..cctor
ScreenshotsManager                 <- CoreSystems.Initialize, CoreSystems..cctor
Settings                           <- CoreSystems.Initialize, CoreSystems..cctor
ShaderAsserts                      <- CoreSystems.Initialize, CoreSystems..cctor
ShaderFileCache                    <- CoreSystems.Initialize, CoreSystems..cctor
ShaderFileReaders                  <- CoreSystems.InitializeSyncSystems, CoreSystems..cctor
Shaders                            <- CoreSystems.Initialize, CoreSystems..cctor
SimpleObjectPool                   <- CoreSystems.Initialize, CoreSystems..cctor
SparseUpdateData                   <- CoreSystems.Initialize, CoreSystems..cctor
SparseUpdateJob                    <- CoreSystems.Initialize, CoreSystems..cctor
SpriteRenderer                     <- CoreSystems.Initialize, CoreSystems..cctor
Stats                              <- CoreSystems.Initialize, CoreSystems..cctor
StreamingStats                     <- CoreSystems.Initialize, CoreSystems..cctor
StructuredBufferTables             <- CoreSystems.Initialize, CoreSystems..cctor
SwapChain                          <- CoreSystems.Initialize, CoreSystems..cctor
TemporaryBuffers                   <- CoreSystems.Initialize, CoreSystems..cctor
Texture2DTables                    <- CoreSystems.Initialize, CoreSystems..cctor
TextureCubeTables                  <- CoreSystems.Initialize, CoreSystems..cctor
Time                               <- CoreSystems.InitializeSyncSystems, CoreSystems..cctor
VectorImages                       <- CoreSystems.Initialize, CoreSystems..cctor
VectorRenderer                     <- CoreSystems.InitializeSyncSystems, CoreSystems..cctor
VideoMemoryMonitor                 <- CoreSystems.Initialize, CoreSystems..cctor
Water                              <- CoreSystems.Initialize, CoreSystems..cctor
_debugDrawRoot                     <- CoreSystems.Initialize, CoreSystems..cctor
_disposeTimeout                    <- CoreSystems..cctor
_globalMeshBuilder                 <- CoreSystems.Initialize, CoreSystems..cctor
_globalMeshBuilder2D               <- CoreSystems.Initialize, CoreSystems..cctor
_listeners                         <- CoreSystems..cctor
_traceChannel                      <- CoreSystems.Initialize
```

## 27. Draw contexts

### `Keen.VRage.Render12.Core.Systems.DrawContextManager`

```
  int field CullingContext <MainViewCulling>k__BackingField
  int field CullingContext <MainViewEffectsCulling>k__BackingField
  int field CullingContext[] <CascadeCulling>k__BackingField
  int field OcclusionContext[] <CascadeOcclusion>k__BackingField
  int field VisibilityListBufferContext[] <CascadeVisibilityLists>k__BackingField
  int field CullingContext[] <CharacterShadowCulling>k__BackingField
  int field CullingContext[] <EnvProbeCulling>k__BackingField
  int field ClusteringContext <MainViewClustering>k__BackingField
  int field ClusteringContext <EnvProbeClustering>k__BackingField
  int field ParticleContext <GPUParticles>k__BackingField
  int field FlaresContext <LensFlares>k__BackingField
  int field CascadeShadowsContext <CascadeShadows>k__BackingField
  int field CharacterShadowsContext <CharacterShadows>k__BackingField
  int field DirectionalLightShadowResources <DirectionalLightShadowResources>k__BackingField
  int field OcclusionContext <Occlusion>k__BackingField
  int field OutputGeometryBufferContext <MainOutputGeometryBuffers>k__BackingField
  int field OutputGeometryBufferContext <MainOutputEffectGeometryBuffers>k__BackingField
  int field VisibilityListBufferContext <MainVisibilityListBuffers>k__BackingField
  int field LODTransitionContext <LODTransitions>k__BackingField
  int field LODTransitionContext <InstancedLODTransitions>k__BackingField
  int field GrassBufferContext <GrassBufferContext>k__BackingField
  int field HiZContext <HiZContext>k__BackingField
  int field VolumeRenderingContext <VolumeRenderingContext>k__BackingField
  int field RTGIContext <RTGIContext>k__BackingField
  int field StochasticTransparencyContext <StochasticTransparencyContext>k__BackingField
  pub field WaterContext WaterContext
  pub field WaterContextLifetimeHelper WaterContextLifetimeHelper
  pub field Buffer`1 LocalLightsToUpdate
  pub field Buffer`1 ShadowMasksToUpdate
  pub field Buffer`1 CascadesToUpdate
  prop CullingContext MainViewCulling
  prop CullingContext MainViewEffectsCulling
  prop CullingContext[] CascadeCulling
  prop OcclusionContext[] CascadeOcclusion
  prop VisibilityListBufferContext[] CascadeVisibilityLists
  prop CullingContext[] CharacterShadowCulling
  prop CullingContext[] EnvProbeCulling
  prop ClusteringContext MainViewClustering
  prop ClusteringContext EnvProbeClustering
  prop ParticleContext GPUParticles
  prop FlaresContext LensFlares
  prop CascadeShadowsContext CascadeShadows
  prop CharacterShadowsContext CharacterShadows
  prop DirectionalLightShadowResources DirectionalLightShadowResources
  prop OcclusionContext Occlusion
  prop OutputGeometryBufferContext MainOutputGeometryBuffers
  prop OutputGeometryBufferContext MainOutputEffectGeometryBuffers
  prop VisibilityListBufferContext MainVisibilityListBuffers
  prop LODTransitionContext LODTransitions
  prop LODTransitionContext InstancedLODTransitions
  pub Void .ctor()
  pub Void Dispose()
  pub CullingContext BorrowShadowCulling(Int32 rootEntityId)
  pub Void ReturnShadowCulling(CullingContext context)
  pub Void OnBeginDraw()
  pub Void OnEndDraw()
  pub Void OnResetContext()
  pub Void OnUpdateStats()
  int Void CreateInitialContexts(TargetStatsData targetStats)
  int Void DisposeContexts()
```

### `DrawContext` — not found

## 28. Route B — GPU readback to a file-backed texture

`DrawImage` rejects generated (render-target) handles but accepts
file-backed guid handles. So the frame has to leave the GPU, land on
disk, and be registered as a resource.

### `Keen.VRage.Render.OutputContracts.RenderOutputManager`

```
  int field RenderDisplaySettings> OnDisplaySettingsChanged
  int field Byte>> OnScreenshotToMemoryTaken
  int field RuntimeModel> OnRuntimeModelUploaded
  int field RenderOutputContracts/ResourcePinReportType> OnResourcePinReport
  int field ExposureData> OnExposureChanged
  int field Action OnLowVideoMemory
  int field RenderOutputContracts/GpuThrottleKind> OnGpuThrottling
  int field RenderOutputContracts/DetectedOverlayFlags> OnDisplaySettingsChangeFailed
  int field Action OnToggleFullscreen
  int field IRenderOutputGameContracts _gameHandler
  int field RenderOutputCommandBuffer _outputCB
  int field IntPtr>[] _handlerTable
  event RenderDisplaySettings> OnDisplaySettingsChanged
  event Byte>> OnScreenshotToMemoryTaken
  event RuntimeModel> OnRuntimeModelUploaded
  event RenderOutputContracts/ResourcePinReportType> OnResourcePinReport
  event ExposureData> OnExposureChanged
  event Action OnLowVideoMemory
  event RenderOutputContracts/GpuThrottleKind> OnGpuThrottling
  event RenderOutputContracts/DetectedOverlayFlags> OnDisplaySettingsChangeFailed
  event Action OnToggleFullscreen
  pub Void add_OnDisplaySettingsChanged(Action`1 value)
  pub Void remove_OnDisplaySettingsChanged(Action`1 value)
  pub Void add_OnScreenshotToMemoryTaken(Action`4 value)
  pub Void remove_OnScreenshotToMemoryTaken(Action`4 value)
  pub Void add_OnRuntimeModelUploaded(Action`1 value)
  pub Void remove_OnRuntimeModelUploaded(Action`1 value)
  pub Void add_OnResourcePinReport(Action`2 value)
  pub Void remove_OnResourcePinReport(Action`2 value)
  pub Void add_OnExposureChanged(Action`1 value)
  pub Void remove_OnExposureChanged(Action`1 value)
  pub Void add_OnLowVideoMemory(Action value)
  pub Void remove_OnLowVideoMemory(Action value)
  pub Void add_OnGpuThrottling(Action`1 value)
  pub Void remove_OnGpuThrottling(Action`1 value)
  pub Void add_OnDisplaySettingsChangeFailed(Action`1 value)
  pub Void remove_OnDisplaySettingsChangeFailed(Action`1 value)
  pub Void add_OnToggleFullscreen(Action value)
  pub Void remove_OnToggleFullscreen(Action value)
  pub Void .ctor(RenderOutputCommandBuffer outputCB)
  pub Void SetGameContractHandler(IRenderOutputGameContracts handler)
  pub Void Process()
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.DisplaySettingsChanged(RenderDisplaySettings settings)
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.ScreenshotToMemoryTaken(RenderId offscreenTextureRenderId, Vector2I resolution, Int32 pitch, Memory`1 outputMemory)
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.RuntimeModelUploaded(RenderId runtimeModelId)
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.ResourcePinReport(RenderId textureResourcePinId, ResourcePinReportType type)
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.ToggleFullscreen()
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.ExposureChanged(ExposureData exposure)
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.WarnLowOnMemory()
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.WarnGpuThrottling(GpuThrottleKind kind)
  int Void Keen.VRage.Render.OutputContracts.IRenderOutputContracts.WarnDisplaySettingsChangeFailed(DetectedOverlayFlags detectedOverlays)
```

### `Keen.VRage.Render.Contracts.OffscreenRenderTarget`

```
  int field RenderId <Id>k__BackingField
  pub Void Dispose()
  pub Void TakeScreenshotToMemory(Boolean waitUntilFullyLoaded)
```

### `Keen.VRage.Library.Filesystem.ContentCache.ContentCache`

```
  pub field String CACHE_FILENAME
  pub field String REFERENCE_CACHE_FILENAME
  int field Object _lock
  int field FileHandle> _resourceHandleToFileHandle
  int field ResourceHandle> _fileHandleToResourceHandle
  int field String> _resourceHandleToMountedId
  int field String> _projectPathToDebugName
  int field ContentCacheResourceLocator _resourceLocator
  int field ContentChangeListener _contentChangeListener
  int field ContentBlobSet> _blobRecords
  int field Type> _extractorTypeToBlobType
  pub Void .ctor()
  pub ListReader`1 GetAssets()
  int Void SetIndexers(HashSetReader`1 types)
  pub Void LoadContentCacheData(ContentBlobData contentCacheData, IFileReader originatingFileSystem, String mountPath)
  int Void LoadBlobData(DictionaryReader`2 blobData, IFileReader originatingFileSystem, HashSet`1 successfullyMappedResourceHandles)
  int Void LoadBlobData(DictionaryReader`2 blobData, IFileReader originatingFileSystem, HashSet`1 successfullyMappedResourceHandles)
  int Void OnMountDismounted()
  int Void InvalidateAndRefillFromContentCaches(IEnumerable`1 currentMounts)
  int Void OnMountAdded(IFileReader fileReader, String mountedPath, String debugName, Boolean ignoreCaches)
  int Task OnContentFileChanged(ResourceHandle handle)
  int Void AddMapping(FileHandle fileHandle, ResourceHandle resourceHandle, String projectMapping)
  int Void RemoveMapping(FileHandle fileHandle, ResourceHandle resourceHandle)
  pub Boolean TryGetData(ResourceHandle resourceHandle, T& data)
  pub T GetData(ResourceHandle resourceHandle)
  int T UpdateData(ResourceHandle resourceHandle)
  int T ExtractData(ResourceHandle resourceHandle, IDesignTimeResourceLocator resourceLocator)
  pub Void CollectRecordedAssetBlobs(ListDictionary`2 target)
  int Boolean TryPeekCacheContent(ResourceHandle resourceHandle, Nullable`1& cacheContent)
  pub Boolean TryTranslateResourceHandle(ResourceHandle resourceHandle, FileHandle& fileHandle)
  int String ReplaceProjectIdentifiersWithDebugNames(String input)
  pub Boolean TryGetProjectIdentifier(ResourceHandle handle, String& identifier)
  pub Boolean TryTranslateFileHandle(FileHandle fileHandle, ResourceHandle& resourceHandle)
  pub ResourceHandle RegisterFile(FileHandle fileHandle, String projectMapping)
  pub Void SetMapping(ResourceHandle resourceHandle, FileHandle fileHandle)
  pub Void Unregister(ResourceHandle resourceHandle)
  pub Task OnBeforeChanged(ResourceHandle handle)
  pub Task OnAfterChanged(ResourceHandle handle)
  int Boolean TryGetFileHandleFromProject(Guid projectGuid, String projectRelativePath, FileHandle& fileHandle)
  int Boolean TryGetHandleProject(FileHandle fileHandle, Guid& projectGuid)
  int Boolean TryGetProjectDebugName(Guid projectGuid, String& debugName)
```

### Screenshot-to-memory callback shape

```
RenderOutputManager.OnScreenshotToMemoryTaken : System.Action`4<Keen.VRage.Render.Contracts.OffscreenRenderTarget,Keen.VRage.Library.Mathematics.Vector2I,System.Int32,System.Memory`1<System.Byte>>
RenderCommand.MainRenderTarget_TakeScreenshot : Keen.VRage.Render.FrameData.RenderCommand
RenderCommand.OffscreenRenderTarget_TakeScreenshotToMemory : Keen.VRage.Render.FrameData.RenderCommand
RenderOutputCommand.RenderOutputContracts_ScreenshotToMemoryTaken : Keen.VRage.Render.FrameData.RenderOutputCommand
FrameManagerSettings.ScreenshotWaitFrames : System.Int32
OffscreenTargetManager._immediatelyScreenshotsToMemory : System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent>
OffscreenTargetManager._fullyLoadedScreenshotsToMemory : System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent>
CoreSystems.ScreenshotsManager : Keen.VRage.Render12.Core.Systems.ScreenshotsManager
ScreenBuffers._screenshotWaitFrameId : System.Int32
ScreenshotsManager._requestedScreenshots : System.Collections.Generic.List`1<Keen.VRage.Render12.Core.Systems.ScreenshotsManager/Screenshot>
ScreenshotsManager._screenshotTasks : System.Collections.Generic.List`1<System.Threading.Tasks.Task>
ScreenshotsManager._screenShotCopyJob : Keen.VRage.Render12.PostProcessStage.CopyJob
<>c__DisplayClass9_0.screenshot : Keen.VRage.Render12.Core.Systems.ScreenshotsManager/Screenshot
ScreenshotMetadata.<Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.VRage.Core.Utils.ScreenshotMetadata>.Cloner>k__BackingField : Keen.VRage.Library.Utils.Cloning.IDeepCloner`1<Keen.VRage.Core.Utils.ScreenshotMetadata>
DetailScreenViewModel.<Screenshots>k__BackingField : System.Collections.ObjectModel.ObservableCollection`1<Keen.VRage.Library.Utils.ResourceHandle`1<Keen.VRage.Core.Render.GUIAsset>>
DetailsScreen.PART_ScreenshotButton : Avalonia.Controls.Button
UGCItemViewModel.<Screenshots>k__BackingField : System.Collections.ObjectModel.ObservableCollection`1<Keen.VRage.Library.Utils.ResourceHandle`1<Keen.VRage.Core.Render.GUIAsset>>
BlueprintAvaloniaConfiguration.<ScreenshotIcon>k__BackingField : Keen.VRage.Library.Utils.ResourceHandle`1<Keen.VRage.Core.Render.GUIAsset>
BlueprintDetailsScreenViewModel._hasNewScreenshot : System.Boolean
BlueprintDetailsScreenViewModel.<TakeScreenshotCommand>k__BackingField : System.Windows.Input.ICommand
BlueprintEditModel.<HasNewScreenshot>k__BackingField : System.Boolean
BlueprintAvaloniaConfigurationObjectBuilder.ScreenshotIcon : Keen.VRage.Library.Utils.ResourceHandle`1<Keen.VRage.Core.Render.GUIAsset>
DialogsConfigurationObjectBuilder.BlueprintScreenshotSavingFailureDialog : Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog.OneOptionDialogDefinition
DialogsConfiguration.<BlueprintScreenshotSavingFailureDialog>k__BackingField : Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog.OneOptionDialogDefinition
BlueprintCreationToolDefinition.<UpdateScreenshotOnReplace>k__BackingField : System.Boolean
ScreenshotToolDefinition.<TakeScreenshot>k__BackingField : Keen.VRage.Input.InputActionDefinition
ScreenshotToolDefinition.<TakeScreenshotInputHintText>k__BackingField : Keen.VRage.Library.Localization.LocKey
ScreenshotToolDefinition.<Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.Game2.Client.GameSystems.ScreenshotToolDefinition>.Cloner>k__BackingField : Keen.VRage.Library.Utils.Cloning.IDeepCloner`1<Keen.Game2.Client.GameSystems.ScreenshotToolDefinition>
ScreenshotToolComponent._takeScreenshot : System.Action`1<Keen.Game2.Client.GameSystems.ScreenshotToolComponent>
ScreenshotToolHandlerDefinition.<Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.Game2.Client.GameSystems.ScreenshotToolHandlerDefinition>.Cloner>k__BackingField : Keen.VRage.Library.Utils.Cloning.IDeepCloner`1<Keen.Game2.Client.GameSystems.ScreenshotToolHandlerDefinition>
BlueprintCreationToolDefinitionObjectBuilder.UpdateScreenshotOnReplace : System.Boolean
ScreenshotToolDefinitionObjectBuilder.TakeScreenshot : Keen.VRage.Input.InputActionDefinition
ScreenshotToolDefinitionObjectBuilder.TakeScreenshotInputHintText : Keen.VRage.Library.Localization.LocKey
ScreenshotToolDefinitionObjectBuilder.<Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.Game2.Client.GameSystems.ScreenshotToolDefinitionObjectBuilder>.Cloner>k__BackingField : Keen.VRage.Library.Utils.Cloning.IDeepCloner`1<Keen.Game2.Client.GameSystems.ScreenshotToolDefinitionObjectBuilder>
ScreenshotToolHandlerDefinitionObjectBuilder.<Keen.VRage.Library.Utils.Cloning.IDeepCloneable<Keen.Game2.Client.GameSystems.ScreenshotToolHandlerDefinitionObjectBuilder>.Cloner>k__BackingField : Keen.VRage.Library.Utils.Cloning.IDeepCloner`1<Keen.Game2.Client.GameSystems.ScreenshotToolHandlerDefinitionObjectBuilder>
Blueprint.SCREENSHOTS_DIRECTORY : System.String
Blueprint.SCREENSHOT_EXTENSION : System.String
BlueprintConfiguration.<MaxScreenshotSizes>k__BackingField : System.Collections.Immutable.ImmutableArray`1<Keen.VRage.Library.Mathematics.Vector2I>
BlueprintExtensions.MAX_SCREENSHOT_SIZE_IN_BYTES : System.Int32
<>c__DisplayClass1_0.tempScreenshotFileHandle : Keen.VRage.Library.Filesystem.FileHandleWritable
<<TakeScreenshotAsync>g__TakeNewScreenshots|2>d.<foundScreenshotResolution>5__2 : System.Boolean
BlueprintConfigurationObjectBuilder.MaxScreenshotSizes : Keen.VRage.Library.Collections.MergeableList`1<Keen.VRage.Library.Mathematics.Vector2I>
<TrySaveWorldSession>d__64.<screenshotTask>5__12 : Keen.VRage.Multiplayer.NetworkStories.NetworkStory
```

### Existing consumers of screenshot-to-memory

```
VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputContracts.ScreenshotToMemoryTaken -> RenderOutputContracts_ScreenshotToMemoryTaken
VRage.Render: Keen.VRage.Render.Contracts.OffscreenRenderTarget.TakeScreenshotToMemory -> OffscreenRenderTarget_TakeScreenshotToMemory
VRage.Render12: Keen.VRage.Render12.SceneSystem.SceneManager.IOffscreenRenderTarget_TakeScreenshotToMemory -> TakeScreenshotToMemory
VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent.TakeScreenshotToMemory -> EnqueueTakingScreenshotToMemory
VRage.Render12: Keen.VRage.Render12.EngineComponents.Render12EngineComponent.SendScreenshotToUser -> ScreenshotToMemoryTaken
```

## 29. Where does a RenderOutputManager instance live?

```
prop   Keen.VRage.Render.EngineComponents.RenderEngineComponent.RenderOutputManager
method Keen.VRage.Render.EngineComponents.RenderEngineComponent.get_RenderOutputManager() -> RenderOutputManager
prop   Keen.VRage.Render12.EngineComponents.Render12EngineComponent.RenderOutputManager
method Keen.VRage.Render12.EngineComponents.Render12EngineComponent.get_RenderOutputManager() -> RenderOutputManager
field  Keen.VRage.Render12.EngineComponents.Render12EngineComponent/MainThread.RenderOutputManager pub
field  Keen.Game2.Client.RuntimeSystems.GpuThrottleNotificationSessionComponent._renderOutputManager int
field  Keen.Game2.Client.RuntimeSystems.VideoMemoryWarningSessionComponent._renderOutputManager int
field  Keen.Game2.Client.WorldObjects.Character.ToggleStatByExposureComponent._renderOutputManager int
```

### Who raises OnScreenshotToMemoryTaken (the delivery path)

```
VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputManager.add_OnScreenshotToMemoryTaken  [ldfld]
VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputManager.remove_OnScreenshotToMemoryTaken  [ldfld]
VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputManager.Keen.VRage.Render.OutputContracts.IRenderOutputContracts.ScreenshotToMemoryTaken  [ldfld]
```

## 30. Who drains the screenshot-to-memory queue

```
VRage.Render: Keen.VRage.Render.OutputContracts.RenderOutputContracts.ScreenshotToMemoryTaken  ->  RenderOutputContracts_ScreenshotToMemoryTaken
VRage.Render12: Keen.VRage.Render12.UIStage.OffscreenUIRenderer.DoWork  ->  TryDequeueNextRenderRequest
VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent.TakeScreenshotToMemory  ->  EnqueueTakingScreenshotToMemory
VRage.Render12: Keen.VRage.Render12.EngineComponents.Render12EngineComponent.SendScreenshotToUser  ->  ScreenshotToMemoryTaken
```

### `OffscreenTargetManager.TryDequeueWork` — full IL

```
  ldsfld         Keen.VRage.Render12.Core.Systems.LoadingMonitor Keen.VRage.Render12.Core.CoreSystems::LoadingMonitor
  callvirt       System.Int64 Keen.VRage.Render12.Core.Systems.LoadingMonitor::get_LoadingCount()
  brtrue.s       IL_0036: ldarg.0
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTarge
  callvirt       System.Int32 System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.Offscre
  brfalse.s      IL_0036: ldarg.0
  ldarg.1        
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTarge
  call           !!0 Keen.VRage.Library.Extensions.CollectionExtensions::First<Keen.VRage.Render12.SceneSystem.Compon
  stind.ref      
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTarge
  ldarg.1        
  ldind.ref      
  callvirt       System.Boolean System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.Offsc
  pop            
  ldc.i4.1       
  ret            
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTarge
  callvirt       System.Int32 System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.Offscre
  brfalse.s      IL_0060: ldarg.1
  ldarg.1        
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTarge
  call           !!0 Keen.VRage.Library.Extensions.CollectionExtensions::First<Keen.VRage.Render12.SceneSystem.Compon
  stind.ref      
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTarge
  ldarg.1        
  ldind.ref      
  callvirt       System.Boolean System.Collections.Generic.HashSet`1<Keen.VRage.Render12.SceneSystem.Components.Offsc
  pop            
  ldc.i4.1       
  ret            
  ldarg.1        
  ldnull         
  stind.ref      
  ldc.i4.0       
  ret            
```

### `OffscreenTargetManager.TryDequeueNextRenderRequest` — full IL

```
  call           System.Void Keen.VRage.Render12.Core.CoreSystems::AssertRenderThread()
  br.s           IL_003e: ldarg.0
  ldarg.0        
  ldfld          System.Collections.Generic.List`1<Keen.VRage.Library.Utils.GeneratedResourceHandle> Keen.VRage.Rende
  ldc.i4.0       
  callvirt       !0 System.Collections.Generic.List`1<Keen.VRage.Library.Utils.GeneratedResourceHandle>::get_Item(Sys
  stloc.0        
  ldarg.0        
  ldfld          System.Collections.Generic.List`1<Keen.VRage.Library.Utils.GeneratedResourceHandle> Keen.VRage.Rende
  ldc.i4.0       
  callvirt       System.Void System.Collections.Generic.List`1<Keen.VRage.Library.Utils.GeneratedResourceHandle>::Rem
  ldarg.0        
  ldfld          System.Collections.Generic.HashSet`1<Keen.VRage.Library.Utils.GeneratedResourceHandle> Keen.VRage.Re
  ldloc.0        
  callvirt       System.Boolean System.Collections.Generic.HashSet`1<Keen.VRage.Library.Utils.GeneratedResourceHandle
  pop            
  ldarg.0        
  ldfld          System.Collections.Generic.Dictionary`2<Keen.VRage.Library.Utils.GeneratedResourceHandle,Keen.VRage.
  ldloc.0        
  ldarg.1        
  callvirt       System.Boolean System.Collections.Generic.Dictionary`2<Keen.VRage.Library.Utils.GeneratedResourceHan
  brfalse.s      IL_003e: ldarg.0
  ldc.i4.1       
  ret            
  ldarg.0        
  ldfld          System.Collections.Generic.List`1<Keen.VRage.Library.Utils.GeneratedResourceHandle> Keen.VRage.Rende
  callvirt       System.Int32 System.Collections.Generic.List`1<Keen.VRage.Library.Utils.GeneratedResourceHandle>::ge
  ldc.i4.0       
  bgt.s          IL_0007: ldarg.0
  ldarg.1        
  ldnull         
  stind.ref      
  ldc.i4.0       
  ret            
```

## 31. What populates the offscreen render-request queue

### `TryDequeueNextRenderRequest` — which field it drains
```
  ldfld      OffscreenTargetManager._pendingRenderList : List`1
  ldfld      OffscreenTargetManager._pendingRenderList : List`1
  ldfld      OffscreenTargetManager._pendingRenderSet : HashSet`1
  ldfld      OffscreenTargetManager._registeredTextures : Dictionary`2
  ldfld      OffscreenTargetManager._pendingRenderList : List`1
```

### Writers of OffscreenTargetManager state
```
VRage.Render12: Keen.VRage.Render12.UIStage.OffscreenUIRenderer.DoWork  ->  TryDequeueNextRenderRequest
VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent.Initialize  ->  RegisterOffscreenTexture
VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent.TakeScreenshotToMemory  ->  EnqueueTakingScreenshotToMemory
VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.OffscreenRenderTargetComponent.OnRemovedFromScene  ->  UnregisterOffscreenTexture
VRage.Render12: Keen.VRage.Render12.SceneSystem.Components.UISystemComponent.ProcessEnqueuedUIChanges  ->  RequestRender
VRage.Render12: Keen.VRage.Render12.EngineComponents.Render12EngineComponent.<Draw>g__DrawInternal|52_0  ->  get_HasPendingRenderRequests
VRage.Render12: Keen.VRage.Render12.Core.CoreSystems.Initialize  ->  .ctor
```

### `OffscreenTargetManager` members
```
  field Dictionary`2 _registeredTextures
  field HashSet`1 _immediatelyScreenshotsToMemory
  field HashSet`1 _fullyLoadedScreenshotsToMemory
  field List`1 _pendingRenderList
  field HashSet`1 _pendingRenderSet
  pub Void Dispose()
  pub Void RegisterOffscreenTexture(OffscreenRenderTargetComponent offscreenRenderTarget)
  pub Void UnregisterOffscreenTexture(OffscreenRenderTargetComponent offscreenRenderTarget)
  pub Void RequestRender(GeneratedResourceHandle handle)
  pub Boolean TryDequeueNextRenderRequest(OffscreenRenderTargetComponent& component)
  pub Void EnqueueTakingScreenshotToMemory(OffscreenRenderTargetComponent offscreenRenderTarget, Boolean waitUtilFullyLoaded)
  pub Boolean TryDequeueWork(OffscreenRenderTargetComponent& offscreenTexture)
  pub Void .ctor()
```

## 32. Tonemapping the feed

### `CopyJob.DoWork` parameters
```
  Keen.VRage.Render12.Core.CommandLists.DirectCommandList commandList
  Keen.VRage.Render12.Resources.Views.IRenderTargetView destination
  Keen.VRage.Render12.Resources.Views.ITexture2DView source
  System.Nullable`1<System.Drawing.Rectangle> viewport
  System.Nullable`1<Keen.VRage.Render12.PostProcessStage.CopyJob/PostProcess> postProcess
  Keen.VRage.Render12.PostProcessStage.CopyJob/Channel channelFlags
  Keen.VRage.Render12.Resources.Views.ITexture2DView opacitySource
  System.Nullable`1<System.Drawing.Rectangle> cropRect
```

### `Keen.VRage.Render12.PostProcessStage.CopyJob/PostProcess` (enum)
```
  Normalize = 0
```

### `Keen.VRage.Render12.PostProcessStage.CopyJob/Channel` (enum)
```
  None = 0
  R = 1
  G = 2
  B = 4
  A = 8
  All = 15
```

### `Keen.VRage.Render12.PostProcessStage.EyeAdaptationJob`
```
  field Int32 HISTOGRAM_THREAD_COUNT_X
  field Int32 HISTOGRAM_THREAD_COUNT_Y
  field Int32 HISTOGRAM_BIN_COUNT
  field Boolean _areAutoExposuresInitialized
  field RWBuffer _histogram
  field RenderTargetTexture[] _autoExposures
  field ComputePSO _updateHistogramPso
  field ComputePSO _updateHistogramScreenCenterPso
  field ScreenQuadJob _constantExposureJob
  field ScreenQuadJob _eyeAdaptationJob
  field ScreenQuadJob _downSampleJob
  field ScreenQuadJob _debugHistogramJob
  field StatReadbackBuffers`1 _outputBuffers
  Void .ctor(List`1 initializationTasks)
  Task InitializeAsync()
  Void Dispose()
  ITexture2DView ConstantExposure(DirectCommandList commandList)
  ITexture2DView DynamicExposure(DirectCommandList commandList, ITexture2DView source, Boolean generateDebugHistogram, Nullable`1& debugHistogram)
  Void OnResetContext()
```

### `TonemappingJob` — not found

### `Keen.VRage.Render12.PostProcessStage.ToneMappingJob`
```
  field Int32 NUM_THREADS
  field RootSignature _rootSignature
  field ComputePSO _psoBasic
  field ComputePSO _psoAlphaLuminance
  field ComputePSO _psoDisableToneMapping
  field Nullable`1 _createPSODisableToneMapping
  Void .ctor(List`1 tasks)
  Task InitializeAsync()
  Task`1 InitializeDisableToneMappingAsync()
  Void DoWork(ComputeCommandList commandList, ITexture2DView hdrSrc, ITexture2DView exposure, ITexture2DView bloom, IRWTexture2DView ldrDst, Boolean writeAlphaLuminance)
```

### Tonemap-ish types
```
VRage.Render: Keen.VRage.Render.OutputContracts.ExposureData
VRage.Render12: Keen.VRage.Render12.PrepareStage.EnvironmentProbeExposureJob
VRage.Render12: Keen.VRage.Render12.PrepareStage.EnvironmentProbeExposureJob/ExposureOutput
VRage.Render12: Keen.VRage.Render12.PostProcessStage.ToneMappingJob
VRage.Render12: Keen.VRage.Render12.PostProcessStage.ToneMappingJob/<InitializeDisableToneMappingAsync>d__8
Game2.Client: Keen.Game2.Client.WorldObjects.Character.ToggleStatByExposureDefinition
Game2.Client: Keen.Game2.Client.WorldObjects.Character.ToggleStatByExposureComponent
Game2.Client: Keen.Game2.Client.WorldObjects.Character.ToggleStatByExposureComponent/ExposureState
Game2.Client: Keen.Game2.Client.WorldObjects.Character.ToggleStatByExposureDefinitionObjectBuilder
Game2.Client: Keen.Game2.Client.WorldObjects.Character.ToggleStatByExposureDefinitionObjectBuilder_Migrations
```

## 33. The main frame's job sequence (fidelity reference)

### `Render12EngineComponent.Draw`
```
  RenderThreadManager.AssertRenderThread
```

### `Render12EngineComponent.<Draw>g__DrawInternal|52_0`
```
  RenderThreadManager.AssertRenderThread
  RenderCommandBatchManager.ReplayBatches
  SceneManager.Tick
  SamplerManager.OnBeginFrame
  ShaderAssertsManager.CheckErrors
  SparseUpdateDataManager.ApplyDataBufferResize
  SparseUpdateJob.PrintTracking
  SparseUpdateJob.DoWork
  ResourceUploadSynchronizationManager.Upload
  SettingsManager.get_IsRaytracingSupportedAndEnabled
  MaterialsManager.RefreshTables
  SettingsManager.OnBeginDraw
  CommonResourcesManager.OnBeginDraw
  ImpostorBakingManager.BakePendingImpostors
  CommonResourcesManager.OnEndDraw
  SettingsManager.OnEndDraw
  IRCacheResourcesManager.get_ContainerIsInitialized
  IRCacheResourcesManager.CreateAndInitializeContainer
  SettingsManager.get_DRS
  CrashGPUJob.DoWork
  SceneDrawSystem.InitRTXJobs
  SceneDrawSystem.ShouldDraw
  DrawContextManager.OnBeginDraw
  OffscreenTargetManager.get_HasPendingRenderRequests
  SceneManager.get_SceneSystems
  SceneDrawSystem.Draw
  DrawContextManager.OnEndDraw
  SceneDrawSystem.ExecuteAccelerationStructuresBuilding
  SceneManager.GetEntitiesOfType
  SettingsManager.get_Overrides
  ShaderAssertsManager.PrepareReadback
  ScreenshotsManager.TakeRequestedScreenshots
  SamplerManager.OnEndFrame
```

### `SceneDrawSystem.Draw`
```
  SceneDrawSystem.ShouldDraw
  SceneDrawSystem.BeginAsyncComputeScope
  SceneDrawSystem.ExecuteAccelerationStructuresBuilding
  SceneDrawSystem.ExecuteScenePreparationAndRender
  SceneDrawSystem.EndAsyncComputeScope
  SettingsManager.get_System
  SceneDrawSystem.ExecuteRaytracingPrepareAndSceneFinalize
  BindableTexturePoolManager.BorrowResizableRWRenderTargetTexture
  SceneDrawSystem.ExecuteLighting
  SceneDrawSystem.ExecuteForwardAndPostProcess
  BindableTexturePoolManager.Return
  ScreenshotsManager.TakeRequestedScreenshots
```

### `IndirectEnvironmentPassJob.DoWork` — what it draws
```
  List`1.get_Count
  Assert.True
  DirectCommandList.ClearDepthStencilView
  DirectCommandList.ClearRenderTargetView
  MatrixD.op_Multiply
  CullingFrustumD.SetMatrix
  CommonResourcesManager.get_IndirectWeatherModifiersCullingContext
  PlanetEnvironmentGroup.FillPlanetEnvironmentSetups
  List`1.get_Item
  BindableBufferManager.CreateTransientConstantBuffer
  EnvironmentProbeManager.get_LastLocalLightAmbient
  DirectionalLightShadowResources.CreateIndirectSetupConstantBuffer
  SettingsManager.get_Environment
  EnvironmentProbeManager.get_CloseIBL
  EnvironmentProbeManager.get_FarIBL
  RootParameterBuilder..ctor
  OutputGeometryBufferContext.get_EntityProxyOutputBuffer
  RootParameterBuilder.AddSRV
  ClusteringContext.get_ClusterCollectBuffer
  ClusteringContext.get_ClusterSpansBuffer
  GPUSceneManager.get_PointLightEntityData
  SparseUpdateData.get_DataBuffer
  GPUSceneManager.get_SpotLightEntityData
  GPUSceneManager.get_CapsuleLightEntityData
  GPUSceneManager.get_AreaLightEntityData
  RootParameterBuilder.AddCBV
  ClusteringContext.get_DynamicStorageSetupBufferReadOnly
  DirectionalLightShadowResources.get_DepthMaps
  LocalLightsManager.get_SingleShadowMaps
  LocalLightsManager.get_CubeShadowMaps
  LocalLightsManager.get_CubeShadowMasks
  CommonResourcesManager.get_AtmosphereLUTTable0
  CommonResourcesManager.get_FrameSettings
  IRenderTargetView.get_Resolution
  ReadOnlySpan`1.op_Implicit
  GeometryPassJob.DrawPSOVariants
  List`1.Clear
  IDisposable.Dispose
```

## 34. The missing stages

### `SceneDrawSystem.ExecuteScenePreparationAndRender`

Signature:
```
  int Void ExecuteScenePreparationAndRender(
      Vector2I finalResolution
  )
```

Calls, in order:
```
  FrameDispatcher.CreateDirectCommandList
  SceneDrawSystem.UpdateVideoPlayer
  SceneDrawSystem.ScenePreparationAndRender
  IDisposable.Dispose
```

### `SceneDrawSystem.ExecuteLighting`

Signature:
```
  int Void ExecuteLighting(
      ResizableRWRenderTargetTexture lBuffer
  )
```

Calls, in order:
```
  FrameDispatcher.CreateDirectCommandList
  ScreenBuffers.get_PreUpscaleResolution
  ResizableRWRenderTargetTexture.Resize
  SceneDrawSystem.UpdateAtmosphere
  DebugViewHelper.GetCurrentEngineDebugView
  DirectCommandList.ClearRenderTargetView
  ScreenBuffers.get_GBuffer
  CopyJob.DoWork
  ScreenBuffers.get_MaxPreUpscaleResolution
  BindableTexturePoolManager.BorrowResizableRenderTargetTexture
  Borrowed`1.get_Resource
  ResizableRenderTargetTexture.Resize
  SceneDrawSystem.ComputeDirectionalLighting
  SceneDrawSystem.ComputeCloudShadows
  SceneDrawSystem.DrawSkybox
  SceneDrawSystem.ApplyAtmosphere
  Borrowed`1.op_Implicit
  SceneDrawSystem.ComputeLocalLights
  SceneDrawSystem.ComputeGI
  BindableTexturePoolManager.Return
  IDisposable.Dispose
```

### `SceneDrawSystem.ExecuteForwardAndPostProcess`

Signature:
```
  int Void ExecuteForwardAndPostProcess(
      ResizableRWRenderTargetTexture lBuffer
      RenderTargetTexture>>& screenshotTexture
      ResizableRWRenderTargetTexture finalLDRBuffer
  )
```

Calls, in order:
```
  FrameDispatcher.CreateDirectCommandList
  SceneDrawSystem.ExecuteForwardPasses
  ScreenshotsManager.get_RequestsDisableUi
  SceneDrawSystem.ExecutePostPasses
  ShaderAssertsManager.PrepareReadback
  IDisposable.Dispose
```

### `SceneDrawSystem.ExecutePostPasses`

Signature:
```
  int Void ExecutePostPasses(
      DirectCommandList commandList
      ResizableRWRenderTargetTexture finalLDRBuffer
      ResizableRWRenderTargetTexture lBuffer
      RenderTargetTexture>>& screenshotTexture
      Boolean saveScreenshotWithoutUi
  )
```

Calls, in order:
```
  CopyCommandList.BeginBlock
  DrawContextManager.get_MainOutputGeometryBuffers
  OutputGeometryBufferContext.Borrow
  SceneDrawSystem.PatchHoles
  SceneDrawSystem.ComputeExposure
  SceneDrawSystem.ProcessPreUpscaleDebugView
  SceneDrawSystem.UpscaleTargetFSR
  SceneDrawSystem.ApplyBloom
  Borrowed`1.op_Implicit
  SceneDrawSystem.ApplyToneMapping
  BindableTexturePoolManager.Return
  SceneDrawSystem.ProcessPostUpscaleDebugView
  SceneDrawSystem.ApplyNonFSRUpscalingAndAA
  SceneDrawSystem.SaveScreenshot
  SceneDrawSystem.DrawUI
  SceneDrawSystem.ReturnBorrowedAndReadbackCounters
  IDisposable.Dispose
```

### `SceneDrawSystem.ExecuteForwardPasses`

Signature:
```
  int Void ExecuteForwardPasses(
      DirectCommandList commandList
      ResizableRWRenderTargetTexture lBuffer
  )
```

Calls, in order:
```
  CopyCommandList.BeginBlock
  SceneDrawSystem.DrawUnlit
  SceneDrawSystem.CheckDebugViewInForward
  ResizableRWRenderTargetTexture.get_Resolution
  OITBuffers..ctor
  DrawContextManager.get_MainOutputGeometryBuffers
  OutputGeometryBufferContext.Borrow
  SettingsManager.get_SSSR
  SSSRSettings.get_EnableOpaqueReflections
  SceneDrawSystem.ComputeSSR
  SceneDrawSystem.ExecuteWaterPrepass
  SettingsManager.get_IsFSREnabledAndAllowed
  UpsamplingJob.get_FSR3ReactiveMask
  Assert.NotNull
  UpsamplingJob.get_FSR3TransparencyCompositionMask
  ValueTuple`2..ctor
  SceneDrawSystem.ExecuteVolumetricPasses
  SettingsManager.get_Overrides
  SceneDrawSystem.RenderTransparent
  DrawContextManager.get_StochasticTransparencyContext
  StochasticTransparencyContext.BeginStochasticTransparencyScope
  Borrowed`1.get_Resource
  SceneDrawSystem.DrawWater
  SceneDrawSystem.ResolveStochasticTransparency
  SceneDrawSystem.RenderHighlightsAndTransparentUnlit
  SceneDrawSystem.RenderHolograms
  SceneDrawSystem.ResolveOIT
  SettingsManager.get_Light
  DebugViewHelper.GetCurrentEngineDebugView
  SceneDrawSystem.RenderFlares
  OITBuffers.Dispose
  SettingsManager.get_Hologram
  HologramPassJob.CopyDepthLate
  OutputGeometryBufferContext.Return
  IDisposable.Dispose
```

## 35. Tonemapping and exposure entry points

### `SceneDrawSystem.ApplyToneMapping`
```
  param DirectCommandList commandList
  param ResizableRWRenderTargetTexture toneMappingInput
  param ResizableRWRenderTargetTexture toneMappingOutput
  param ITexture2DView exposure
  param ResizableRenderTargetTexture bloom
  --- reads/calls ---
  CopyCommandList.BeginBlock
  CoreSystems.Settings
  SettingsManager.get_PostProcess
  DebugViewHelper.GetCurrentEngineDebugView
  ResizableRWRenderTargetTexture.GetRWTexture2DView
  SettingsManager.get_DRS
  ToneMappingJob.DoWork
  CopyJob.DoWork
  IDisposable.Dispose
  === reads global ScreenBuffers: no — callable with our own textures
```

### `SceneDrawSystem.ComputeExposure`
```
  param DirectCommandList commandList
  param ResizableRWRenderTargetTexture lBuffer
  param ITexture2DView& exposure
  param RenderTargetTexture>>& debugHistogram
  --- reads/calls ---
  CopyCommandList.BeginBlock
  CoreSystems.Settings
  SettingsManager.get_PostProcess
  DebugViewHelper.GetCurrentEngineDebugView
  SettingsManager.get_Debug
  EyeAdaptationJob.DynamicExposure
  EyeAdaptationJob.ConstantExposure
  EyeAdaptationJob.get_ExposureValues
  EnvironmentProbeExposureJob.get_Exposure
  RenderOutputContracts.ExposureChanged
  IDisposable.Dispose
  === reads global ScreenBuffers: no — callable with our own textures
```

### `SceneDrawSystem.ApplyBloom`
```
  param DirectCommandList commandList
  param ResizableRWRenderTargetTexture toneMappingInput
  param ITexture2DView exposure
  param ResizableRenderTargetTexture>& bloom
  --- reads/calls ---
  CopyCommandList.BeginBlock
  CoreSystems.Settings
  SettingsManager.get_PostProcess
  DebugViewHelper.GetCurrentEngineDebugView
  BloomJob.DoWork
  CoreSystems.BindableTexturePool
  Colors.get_Black
  Color4.op_Implicit
  Color..ctor
  Nullable`1..ctor
  BindableTexturePoolManager.BorrowResizableRenderTargetTexture
  Borrowed`1.get_Resource
  DirectCommandList.ClearRenderTargetView
  IDisposable.Dispose
  === reads global ScreenBuffers: no — callable with our own textures
```

### `SceneDrawSystem.PatchHoles`
```
  param DirectCommandList commandList
  param ResizableRWRenderTargetTexture lBuffer
  --- reads/calls ---
  CoreSystems.Settings
  SettingsManager.get_PostProcess
  CopyCommandList.BeginBlock
  CoreSystems.ScreenBuffers
  ScreenBuffers.get_DepthStencilBuffer
  PatchHolesJob.DoJob
  IDisposable.Dispose
  === reads global ScreenBuffers: YES — needs a buffer swap
```

### `SceneDrawSystem.DrawSkybox`
```
  param DirectCommandList commandList
  param ResizableRWRenderTargetTexture lBuffer
  --- reads/calls ---
  CopyCommandList.BeginBlock
  SkyboxMotionVectorsJob.DoWork
  IDisposable.Dispose
  === reads global ScreenBuffers: no — callable with our own textures
```

## 36. Resource state transitions

### `Keen.VRage.Render12.Core.CommandLists.CopyCommandList`
```
  pub Void ExplicitStateTransition(AutoResourceState autoResourceState, ResourceStates stateAfter, Boolean discardResource)
  int Void RequestResourceState(AutoResourceState autoResourceState, ResourceStates stateAfter, Boolean discardResource)
  int Void RequestUAVBarrier(ResizableRWBuffer uavView)
  int Void RequestUAVBarrier(TemporaryRWBuffer uavView)
  int Void RequestUAVBarrier(RASBuffer rasBuffer)
  pub Void SkipUAVBarrier(AutoResourceState autoResourceState)
  pub Void RequestResourceStateD3D(ID3D12Resource d3dResource, String debugName, ResourceStates stateBefore, ResourceStates stateAfter)
  pub Void RequestAliasingBarrierD3D(ID3D12Resource before, ID3D12Resource after, String debugName)
  pub Void ResolveResourceStates()
  pub Void CopyResource(ICopyDestinationView destination, ICopySourceView source)
  pub Void CopyBufferRegion(ICopyDestinationView destination, Int32 destinationOffset, ICopySourceView source, Int32 sourceOffset, Int32 numBytes)
  pub Void CopyBufferRegionD3D(ID3D12Resource d3dDestination, Int32 destinationOffset, ID3D12Resource d3dSource, Int32 sourceOffset, Int32 numBytes)
  pub Void CopyTextureRegionD3D(ID3D12Resource d3dDestination, Int32 destinationSubresourceIndex, Int32 destinationX, Int32 destinationY, Int32 destinationZ, ID3D12Resource d3dSource, PlacedSubresourceFootPrint sourcePlacedFootprint, Nullable`1 sourceBox)
  pub Void CopySubresource(ICopyDestinationView destination, ICopySourceView source, Int32 sourceSubresourceOffset)
  pub Void CopyTextureSubresource(ICopyDestinationView destination, Int32 destSubresourceOffset, ICopySourceView source, Int32 sourceSubresourceOffset)
```

### `Keen.VRage.Render12.Core.CommandLists.DirectCommandList`
```
```

### `Keen.VRage.Render12.Core.CommandLists.ComputeCommandList`
```
```

### `Keen.VRage.Render12.Resources.StateResolver.ResourceStateMonitor`
```
```

### `OffscreenUIRenderer.DrawOne` — full call order
```
  OffscreenRenderTargetComponent.get_Handle
  OffscreenRenderTargetComponent.get_Resolution
  UISystemComponent.RecordBatches
  UIBatcher.BeginDraw
  OffscreenRenderTargetComponent.get_Format
  OffscreenRenderTargetComponent.get_Format
  OffscreenRenderTargetComponent.get_MipLevels
  Color..ctor
  Nullable`1..ctor
  BindableTexturePoolManager.BorrowRWRenderTargetTexture
  Borrowed`1.get_Resource
  DirectCommandList.ClearRenderTargetView
  Viewport..ctor
  Vector2..ctor
  List`1.GetEnumerator
  Enumerator.get_Current
  Nullable`1.get_Value
  IUIBatch.Draw
  Enumerator.MoveNext
  IDisposable.Dispose
  OffscreenRenderTargetComponent.get_MipLevels
  MipMapJobExtensions.DoWork
  OffscreenRenderTargetComponent.get_Texture
  CopyCommandList.CopyResource
  BindableTexturePoolManager.Return
  UIBatcher.EndDrawKeepBuffers
```

## 37. AutoResourceState — how to get one from a texture

### `Keen.VRage.Render12.Resources.StateResolver.AutoResourceState`
```
  field String GROUP_NAME
  field Int32 INVALID_UAV_BARRIER
  field Int32 _storageID
  field IToken <Token>k__BackingField
  field String _debugName
  field ID3D12Resource _d3dResource
  field Boolean _autoPromotionState
  field Boolean _autoUAVBarrier
  field Boolean _isRaytracingAccelerationStructure
  field Int32 _lastReplayedCommandListIndex
  field Int32 _lastReplayedWriteOnCommandListIndex
  field AccessType _lastAccessType
  field ResourceStates _state
  field InlineList`2 _transitionRequestIds
  field InlineList`2 _usedRequestIds
  field Reporter _reporter
  field DiscardResourceExpectancy _discardResourceExpectancy
  field Int32 _lastReplayedUAVCommandListIndex
  field Action`1 _onStateChanged
  field Int32 _onStateChangedCount
  ctor()
```

### Members returning AutoResourceState
```
prop  IConstantBufferView.AutoResourceState
meth  IConstantBufferView.get_AutoResourceState()
prop  IIndexBufferView.AutoResourceState
meth  IIndexBufferView.get_AutoResourceState()
prop  IRWCountedStructuredBufferView.AutoResourceState
meth  IRWCountedStructuredBufferView.get_AutoResourceState()
prop  IRWStructuredBufferView.AutoResourceState
meth  IRWStructuredBufferView.get_AutoResourceState()
prop  IRWTexture2DArrayView.AutoResourceState
meth  IRWTexture2DArrayView.get_AutoResourceState()
prop  IRWTexture2DView.AutoResourceState
meth  IRWTexture2DView.get_AutoResourceState()
prop  IStructuredBufferView.AutoResourceState
meth  IStructuredBufferView.get_AutoResourceState()
prop  ITexture2DArrayView.AutoResourceState
meth  ITexture2DArrayView.get_AutoResourceState()
prop  ITexture2DView.AutoResourceState
meth  ITexture2DView.get_AutoResourceState()
prop  ITextureCubeView.AutoResourceState
meth  ITextureCubeView.get_AutoResourceState()
prop  IVertexBufferView.AutoResourceState
meth  IVertexBufferView.get_AutoResourceState()
prop  ResizableUploadBuffer.AutoResourceState
meth  ResizableUploadBuffer.get_AutoResourceState()
prop  ResizableUploadTexture.AutoResourceState
meth  ResizableUploadTexture.get_AutoResourceState()
prop  DirectBuffer.AutoResourceState
meth  DirectBuffer.get_AutoResourceState()
prop  ChunkBuffer.AutoResourceState
meth  ChunkBuffer.get_AutoResourceState()
prop  MicroBuffer.AutoResourceState
meth  MicroBuffer.get_AutoResourceState()
meth  ResourceTableBase`3.GetAutoResourceState()
meth  RWTexture2DTable.GetAutoResourceState()
meth  StructuredBufferTable.GetAutoResourceState()
meth  Texture2DTable.GetAutoResourceState()
meth  TextureCubeTable.GetAutoResourceState()
prop  BackBuffer.AutoResourceState
field BackBuffer._autoResourceState int
meth  BackBuffer.get_AutoResourceState()
prop  DepthStencilCubeTexture.AutoResourceState
field DepthStencilCubeTexture._autoResourceState int
meth  DepthStencilCubeTexture.get_AutoResourceState()
prop  CubeTextureSlice.AutoResourceState
meth  CubeTextureSlice.get_AutoResourceState()
prop  DepthStencilTexture.AutoResourceState
field DepthStencilTexture._autoResourceState int
meth  DepthStencilTexture.get_AutoResourceState()
prop  Texture2DSlice.AutoResourceState
meth  Texture2DSlice.get_AutoResourceState()
prop  RenderTargetCubeTexture.AutoResourceState
field RenderTargetCubeTexture._autoResourceState int
meth  RenderTargetCubeTexture.get_AutoResourceState()
prop  RenderTargetTexture.AutoResourceState
field RenderTargetTexture._autoResourceState int
meth  RenderTargetTexture.get_AutoResourceState()
prop  RenderTargetTextureArray.AutoResourceState
field RenderTargetTextureArray._autoResourceState int
meth  RenderTargetTextureArray.get_AutoResourceState()
prop  ResizableDepthStencilTexture.AutoResourceState
field ResizableDepthStencilTexture._autoResourceState int
meth  ResizableDepthStencilTexture.get_AutoResourceState()
```

### `RWRenderTargetTexture` members
```
  -- RWRenderTargetTexture --
    prop IToken Token
    prop Format Keen.VRage.Render12.Resources.Views.ITexture2DView.Format
    prop Format Keen.VRage.Render12.Resources.Views.IRenderTargetView.Format
    prop Format Keen.VRage.Render12.Resources.Views.IRWTexture2DView.Format
    prop Vector2I Resolution
    prop Int32 MipMap
    prop Int32 MipLevels
    prop String Name
    prop Int32 ByteSize
    prop ID3D12Resource D3DResource
    prop ResourceDescription D3DResourceDescription
    prop AutoResourceState AutoResourceState
    prop ShaderResourceViewDescription D3DSRVDescription
    prop UnorderedAccessViewDescription D3DUAVDescription
    prop GpuDescriptorHandle D3DSRVGPUDescriptorHandle
    prop GpuDescriptorHandle D3DUAVGPUDescriptorHandle
    prop CpuDescriptorHandle D3DRTVCpuDescriptorHandle
    prop Color D3DClearColor
    field IToken <Token>k__BackingField
    field String _debugName
    field Vector2I _resolution
    field Format _resourceFormat
    field Format _rtvFormat
    field Format _srvFormat
    field Format _uavFormat
    field Int32 _mipMap
    field ShaderResourceViewDescription _d3dSRVDescription
    field UnorderedAccessViewDescription _d3dUAVDescription
    field Color _d3dClearColor
    field D3DCommittedResourceWrap _d3dResourceWrap
    field AutoResourceState _autoResourceState
    field SRVDescriptor _srvDescriptor
    field UAVDescriptor _uavDescriptor
    field RTVDescriptor _rtvDescriptor
    field List`1 _slices
  -- Object --
```

## 38. Binding our own target to the panel material

### `LcdPanelSurfaceContext.SetNewScreenMaterialHandle`
```
  param LcdContentRendererSessionComponent renderer
  param PBRMaterialDefinition materialDefinition
  param Single aspectRatio
  param LcdScreenOrientation orientation
  param TextureAsset>> colorMetalOverride
  --- calls ---
  LcdPanelSurfaceContext.ReleaseScreenMaterialHandle
  LcdContentRendererSessionComponent.CreateRuntimeLcdMaterial
  Nullable`1..ctor
  fld LcdPanelSurfaceContext._screenMaterialHandle
```

### `LcdContentRendererSessionComponent.CreateRuntimeLcdMaterial`
```
  param PBRMaterialDefinition baseMaterial
  param Single aspectRatio
  param LcdScreenOrientation orientation
  param TextureAsset>> colorMetalOverride
  --- calls ---
  DefinitionHelper.CreateObjectBuilder
  RuntimeDefinitionHelper.Create
  MaterialDefinition.get_DefaultState
  MaterialDefinition.SetRuntimeState
  LCDMaterialDefinition.AssignToDefinition
  PBRMaterialDefinition.AssignToDefinition
  Nullable`1.get_HasValue
  Nullable`1.get_Value
  Nullable`1..ctor
  PBRMaterialDefinition.set_ColorMetalTexture
  LCDMaterialDefinition.set_Orientation
  LCDMaterialDefinition.set_ScreenAspectRatio
  fld LcdContentRendererSessionComponent._renderContracts
  RuntimeMaterialHandle`1..ctor
```

### `LcdPanelSurfaceRenderComponent.UpdateMaterialReplacements`
```
  --- calls ---
  fld LcdPanelSurfaceRenderComponent._surfaces
  fld LcdPanelSurfaceRenderComponent._render
  BlockRenderComponent.SetMaterialReplacements
  LcdPanelSurfaceRenderComponent.ApplyHiddenParts
  List`1..ctor
  HashSet`1..ctor
  fld LcdPanelSurfaceContext.CurrentMaterialState
  LcdPanelSurfaceContext.get_Definition
  fld LcdPanelSurface.UseOnlineTexture
  fld LcdPanelSurface.MeshPartName
  HashSet`1.Add
  LcdPanelSurfaceContext.get_ScreenMaterial
  fld LcdPanelSurfaceRenderComponent._lcdBlock
  LcdMultiPanelComponent.get_Definition
  LcdMultiPanelDefinition.get_PowerOffMaterial
  fld LcdPanelSurface.DefaultScreenMaterial
  MeshPartMaterialPair..ctor
  List`1.Add
  fld LcdPanelSurfaceRenderComponent._modelComponent
  fld BlockModelComponent.Definition
  ModelDefinition.get_Model
  Dictionary`2..ctor
  ImmutableArray.ToImmutableArray
  Dictionary`2.set_Item
  DictionaryReader`2.op_Implicit
  Nullable`1..ctor
```

### Where TransitionToCustomRender sources its material
```
  fld LcdPanelSurfaceContext.CurrentMaterialState : LcdPanelRenderState
  fld LcdPanelSurface.DefaultScreenMaterial : PBRMaterialDefinition
  call LcdPanelSurfaceContext.SetNewScreenMaterialHandle
  fld LcdPanelSurfaceContext.CurrentMaterialState : LcdPanelRenderState
  call LcdPanelSurfaceRenderComponent.UpdateMaterialReplacements
```

## 39. DrawContextManager per-frame lifecycle

### `DrawContextManager.BorrowShadowCulling`
```
  fld  DrawContextManager._unusedShadowCulling : Stack`1
  call Stack`1.TryPop
  brtrue.s
  fld  CoreSystems.Stats : StatManager
  fld  StatManager.DrawStats : DrawStatsData
  fld  DrawStatsData.LocalLights : LocalLightsData
  fld  LocalLightsData.Shadow : StatKey
  call Nullable`1..ctor
  call DrawContextManager.get_SharedGeometryContextCounters
  call DrawContextManager.get_SharedEntityProxyCounters
  call DrawContextManager.get_SharedGeometryStatsData
  call CullingContext..ctor
  fld  DrawContextManager._allShadowCulling : List`1
  call List`1.Add
  call CullingContext.set_RootEntityId
```

### `DrawContextManager.ReturnShadowCulling`
```
  fld  DrawContextManager._unusedShadowCulling : Stack`1
  call Stack`1.Push
```

### All DrawContextManager methods
```
  pub Void .ctor()
  pub Void Dispose()
  pub CullingContext BorrowShadowCulling(Int32 rootEntityId)
  pub Void ReturnShadowCulling(CullingContext context)
  pub Void OnBeginDraw()
  pub Void OnEndDraw()
  pub Void OnResetContext()
  pub Void OnUpdateStats()
  int Void CreateInitialContexts(TargetStatsData targetStats)
  int Void DisposeContexts()
```

### `CullingContext` members
```
  pub Void .ctor(String, Nullable`1, SharedReadableBuffer`1, SharedReadableBuffer`1, SharedReadableBuffer`1, Boolean)
  pub Void Dispose()
  pub Void UpdateRanges(ComputeCommandList, RangeStats&)
  pub Void OnUpdateStats()
```

