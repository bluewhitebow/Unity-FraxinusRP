using Fraxinus.GPUBatcher;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fraxinus.GPUBatcher.Editor
{
    /// <summary>
    /// Prefab authoring helpers retained under the historical type name.
    /// Managers are plain runtime objects now, so this is deliberately not a
    /// UnityEditor.Editor and can never be registered as a custom inspector.
    /// </summary>
    public static class GPUInstancerPrefabManagerEditor
    {
        public static GPUInstancerPrefabPrototype AddPickerObject(
            GPUInstancerPrefabManager prefabManager,
            UnityEngine.Object pickerObject,
            GPUInstancerPrototype overridePrototype = null)
        {
            if (prefabManager == null || pickerObject == null)
                return null;

            if (overridePrototype != null && GPUInstancerDefines.previewCache != null)
                GPUInstancerDefines.previewCache.RemovePreview(overridePrototype);

            if (prefabManager.prefabList == null)
                prefabManager.prefabList = new List<GameObject>();
            if (prefabManager.prototypeList == null)
                prefabManager.prototypeList = new List<GPUInstancerPrototype>();

            GPUInstancerPrefabPrototype pickedPrototype = pickerObject as GPUInstancerPrefabPrototype;
            if (pickedPrototype != null && pickedPrototype.prefabObject != null)
                pickerObject = pickedPrototype.prefabObject;

            GameObject prefabObject = pickerObject as GameObject;
            if (prefabObject == null)
            {
                ShowUnsupportedPrefabWarning(pickerObject);
                return null;
            }

#if UNITY_2018_3_OR_NEWER
            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(prefabObject);
            if (prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant)
            {
                if (PrefabUtility.IsPartOfNonAssetPrefabInstance(prefabObject))
                    prefabObject = GPUInstancerUtility.GetOutermostPrefabAssetRoot(prefabObject);
            }
            else
            {
                ShowUnsupportedPrefabWarning(prefabObject);
                return null;
            }

            if (prefabType == PrefabAssetType.Variant)
            {
                GameObject correspondingPrefab =
                    GPUInstancerUtility.GetCorrespongingPrefabOfVariant(prefabObject);
                PrefabAssetType correspondingType =
                    PrefabUtility.GetPrefabAssetType(correspondingPrefab);
                if (correspondingType != PrefabAssetType.Model &&
                    prefabObject.GetComponent<GPUInstancerPrefab>() == null &&
                    !EditorUtility.DisplayDialog(
                        "Variant Prefab Warning",
                        "Prefab is a Variant. Do you wish to add the Variant as a prototype or the corresponding Prefab?" +
                        "\n\nIt is recommended to add the Prefab if the Variant does not have different renderers.",
                        "Add Variant",
                        "Add Prefab"))
                {
                    prefabObject = correspondingPrefab;
                }
            }
#else
            PrefabType prefabType = PrefabUtility.GetPrefabType(prefabObject);
            if (prefabType != PrefabType.Prefab)
            {
                bool instanceFound = false;
                if (prefabType == PrefabType.PrefabInstance)
                {
#if UNITY_2018_2_OR_NEWER
                    GameObject source = (GameObject)PrefabUtility.GetCorrespondingObjectFromSource(prefabObject);
#else
                    GameObject source = (GameObject)PrefabUtility.GetPrefabParent(prefabObject);
#endif
                    if (source != null && PrefabUtility.GetPrefabType(source) == PrefabType.Prefab)
                    {
                        while (source.transform.parent != null)
                            source = source.transform.parent.gameObject;
                        prefabObject = source;
                        instanceFound = true;
                    }
                }
                if (!instanceFound)
                {
                    ShowUnsupportedPrefabWarning(prefabObject);
                    return null;
                }
            }
#endif

            if (prefabManager.prefabList.Contains(prefabObject))
            {
                GPUInstancerPrefab existing = prefabObject.GetComponent<GPUInstancerPrefab>();
                SyncProfileFromManager(prefabManager);
                return existing != null ? existing.prefabPrototype : null;
            }

            GPUInstancerPrefab prefabScript = prefabObject.GetComponent<GPUInstancerPrefab>();
            if (prefabScript != null && prefabScript.prefabPrototype != null &&
                prefabScript.prefabPrototype.prefabObject != prefabObject)
            {
#if UNITY_2018_3_OR_NEWER
                GPUInstancerUtility.RemoveComponentFromPrefab<GPUInstancerPrefab>(prefabObject);
#else
                UnityEngine.Object.DestroyImmediate(prefabScript, true);
#endif
                prefabScript = null;
            }

            if (prefabScript == null)
            {
#if UNITY_2018_3_OR_NEWER
                prefabScript = GPUInstancerUtility.AddComponentToPrefab<GPUInstancerPrefab>(prefabObject);
#else
                prefabScript = prefabObject.AddComponent<GPUInstancerPrefab>();
#endif
            }
            if (prefabScript == null)
                return null;

            if (!prefabManager.prefabList.Contains(prefabObject))
            {
                prefabManager.prefabList.Add(prefabObject);
                prefabManager.GeneratePrototypes();
            }

            if (prefabScript.prefabPrototype != null)
            {
                if (prefabManager.registeredPrefabs == null)
                    prefabManager.registeredPrefabs = new List<RegisteredPrefabsData>();

                RegisteredPrefabsData data = prefabManager.registeredPrefabs.Find(
                    entry => entry != null &&
                        entry.prefabPrototype == prefabScript.prefabPrototype);
                if (data == null)
                {
                    data = new RegisteredPrefabsData(prefabScript.prefabPrototype);
                    prefabManager.registeredPrefabs.Add(data);
                }

                // Standalone editor integrations may still use this helper.
                // Profile-owned managers intentionally never persist scene
                // component references into ProjectSettings.
                if (prefabManager.profile == null)
                {
                    GPUInstancerPrefab[] instances =
                        UnityEngine.Object.FindObjectsOfType<GPUInstancerPrefab>();
                    foreach (GPUInstancerPrefab instance in instances)
                    {
                        if (instance.prefabPrototype == prefabScript.prefabPrototype &&
                            !data.registeredPrefabs.Contains(instance))
                            data.registeredPrefabs.Add(instance);
                    }
                }
            }

            SyncProfileFromManager(prefabManager);
            return prefabScript.prefabPrototype;
        }

        public static void SetRenderersEnabled(
            GPUInstancerPrefabPrototype prefabPrototype,
            bool enabled)
        {
            if (prefabPrototype == null || prefabPrototype.prefabObject == null)
                return;

#if UNITY_2018_3_OR_NEWER
            GameObject prefabContents =
                GPUInstancerUtility.LoadPrefabContents(prefabPrototype.prefabObject);
#else
            GameObject prefabContents = prefabPrototype.prefabObject;
#endif
            if (prefabContents == null)
                return;

            MeshRenderer[] meshRenderers =
                prefabContents.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer meshRenderer in meshRenderers)
                meshRenderer.enabled = enabled;

            BillboardRenderer[] billboardRenderers =
                prefabContents.GetComponentsInChildren<BillboardRenderer>(true);
            foreach (BillboardRenderer billboardRenderer in billboardRenderers)
                billboardRenderer.enabled = enabled;

            LODGroup lodGroup = prefabContents.GetComponent<LODGroup>();
            if (lodGroup != null)
                lodGroup.enabled = enabled;

            if (prefabPrototype.hasRigidBody)
            {
                Rigidbody rigidbody = prefabContents.GetComponent<Rigidbody>();
                if (enabled || prefabPrototype.autoUpdateTransformData)
                {
                    if (rigidbody == null && prefabPrototype.rigidbodyData != null)
                    {
                        GPUInstancerPrefabPrototype.RigidbodyData data =
                            prefabPrototype.rigidbodyData;
                        rigidbody = prefabContents.AddComponent<Rigidbody>();
                        rigidbody.useGravity = data.useGravity;
                        rigidbody.angularDrag = data.angularDrag;
                        rigidbody.mass = data.mass;
                        rigidbody.constraints = data.constraints;
                        rigidbody.detectCollisions = true;
                        rigidbody.drag = data.drag;
                        rigidbody.isKinematic = data.isKinematic;
                        rigidbody.interpolation = data.interpolation;
                    }
                }
                else if (rigidbody != null)
                {
                    UnityEngine.Object.DestroyImmediate(rigidbody, true);
                }
            }

#if UNITY_2018_3_OR_NEWER
            GPUInstancerUtility.UnloadPrefabContents(
                prefabPrototype.prefabObject, prefabContents, true);
#endif
            EditorUtility.SetDirty(prefabPrototype.prefabObject);
            prefabPrototype.meshRenderersDisabled = !enabled;
            EditorUtility.SetDirty(prefabPrototype);
        }

        public static void CheckPrefabRigidbodies(GPUInstancerPrefabPrototype prototype)
        {
            if (prototype == null || prototype.prefabObject == null ||
                prototype.meshRenderersDisabled)
                return;

            Rigidbody rigidbody = prototype.prefabObject.GetComponent<Rigidbody>();
            bool changed = prototype.hasRigidBody != (rigidbody != null);
            prototype.hasRigidBody = rigidbody != null;
            if (rigidbody != null)
            {
                if (prototype.rigidbodyData == null)
                {
                    prototype.rigidbodyData = new GPUInstancerPrefabPrototype.RigidbodyData();
                    changed = true;
                }
                GPUInstancerPrefabPrototype.RigidbodyData data = prototype.rigidbodyData;
                changed |= data.useGravity != rigidbody.useGravity ||
                    data.angularDrag != rigidbody.angularDrag ||
                    data.mass != rigidbody.mass ||
                    data.constraints != rigidbody.constraints ||
                    data.drag != rigidbody.drag ||
                    data.isKinematic != rigidbody.isKinematic ||
                    data.interpolation != rigidbody.interpolation;
                data.useGravity = rigidbody.useGravity;
                data.angularDrag = rigidbody.angularDrag;
                data.mass = rigidbody.mass;
                data.constraints = rigidbody.constraints;
                data.drag = rigidbody.drag;
                data.isKinematic = rigidbody.isKinematic;
                data.interpolation = rigidbody.interpolation;
            }

            if (changed)
                EditorUtility.SetDirty(prototype);
        }

        private static void ShowUnsupportedPrefabWarning(UnityEngine.Object pickerObject)
        {
#if UNITY_2018_3_OR_NEWER
            bool isModel = pickerObject != null &&
                PrefabUtility.GetPrefabAssetType(pickerObject) == PrefabAssetType.Model;
#else
            bool isModel = pickerObject != null &&
                PrefabUtility.GetPrefabType(pickerObject) == PrefabType.ModelPrefab;
#endif
            EditorUtility.DisplayDialog(
                GPUInstancerConstants.TEXT_PREFAB_TYPE_WARNING_TITLE,
                isModel
                    ? GPUInstancerConstants.TEXT_PREFAB_TYPE_WARNING_3D
                    : GPUInstancerConstants.TEXT_PREFAB_TYPE_WARNING,
                GPUInstancerConstants.TEXT_OK);
        }

        private static void SyncProfileFromManager(GPUInstancerPrefabManager manager)
        {
            GPUInstancerPrefabProfile profile =
                manager != null ? manager.profile as GPUInstancerPrefabProfile : null;
            if (profile == null)
                return;

            profile.prefabList = new List<GameObject>();
            if (manager.prefabList != null)
            {
                foreach (GameObject source in manager.prefabList)
                {
                    GameObject persistentPrefab;
                    if (GPUInstancerPrefabProfileDrawer.TryGetPersistentPrefab(
                        source, out persistentPrefab) &&
                        !profile.prefabList.Contains(persistentPrefab))
                        profile.prefabList.Add(persistentPrefab);
                }
            }

            profile.prototypeList = new List<GPUInstancerPrototype>();
            if (manager.prototypeList != null)
            {
                foreach (GPUInstancerPrototype prototype in manager.prototypeList)
                {
                    if (GPUInstancerProfileEditorUtility.IsPersistentPrototypeAsset(prototype) &&
                        !profile.prototypeList.Contains(prototype))
                        profile.prototypeList.Add(prototype);
                }
            }

            profile.registeredPrefabs = new List<RegisteredPrefabsData>();
            if (manager.registeredPrefabs != null)
            {
                foreach (RegisteredPrefabsData source in manager.registeredPrefabs)
                {
                    if (source == null || source.prefabPrototype == null)
                        continue;
                    RegisteredPrefabsData copy = new RegisteredPrefabsData(source.prefabPrototype);
                    if (source.registeredPrefabs != null)
                    {
                        foreach (GPUInstancerPrefab instance in source.registeredPrefabs)
                        {
                            if (GPUInstancerProfileEditorUtility.IsPersistentPrefabComponent(instance))
                                copy.registeredPrefabs.Add(instance);
                        }
                    }
                    profile.registeredPrefabs.Add(copy);
                }
            }

            GPUInstancerProjectSettings settings =
                GPUInstancerProfileEditorUtility.GetProjectSettings();
            if (settings != null && settings.runtimeConfig != null &&
                settings.runtimeConfig.profiles != null &&
                settings.runtimeConfig.profiles.Contains(profile))
                EditorUtility.SetDirty(settings);
        }
    }
}
