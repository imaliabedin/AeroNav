(() => {
  const STORAGE_KEY = 'skyrl-environment-v1';
  const UNITY_X_OFFSET = 40;
  const UNITY_Z_OFFSET = 30;
  const UNITY_X_SCALE = 0.8;
  const UNITY_Z_SCALE = 0.6;
  const catalog = [
    { name: 'Sedan', size: 4.5, kind: 'car', icon: '▰', color: 0x6688bf },
    { name: 'SUV', size: 4.8, kind: 'car', icon: '▰', color: 0x8b9bb2 },
    { name: 'Taxi', size: 4.5, kind: 'car', icon: '▰', color: 0xd8b83f },
    { name: 'Police car', size: 4.7, kind: 'car', icon: '▰', color: 0xe7eaf0 },
    { name: 'Van', size: 5.2, kind: 'car', icon: '▰', color: 0xb3c2d6 },
    { name: 'Pine tree', size: 7, kind: 'pine', icon: '♠', color: 0x3b9b71 },
    { name: 'Round tree', size: 5, kind: 'tree', icon: '♣', color: 0x4d9e68 },
    { name: 'Bush', size: 1.6, kind: 'bush', icon: '✿', color: 0x54a66e },
    { name: 'Street light', size: 5.5, kind: 'light', icon: '⌁', color: 0xc4d8ff },
    { name: 'Traffic cone', size: 0.7, kind: 'cone', icon: '△', color: 0xff8b44 },
    { name: 'Phone', size: 0.15, kind: 'phone', icon: '▯', color: 0x8798b4 },
    { name: 'Keys', size: 0.12, kind: 'keys', icon: '⚿', color: 0xd6bd67 },
    { name: 'Wallet', size: 0.18, kind: 'wallet', icon: '▰', color: 0x815d4d }
  ];
  const itemByName = new Map(catalog.map(item => [item.name, item]));
  const mount = document.getElementById('mount');
  const objectList = document.getElementById('objectList');
  const builderStatus = document.getElementById('builderStatus');
  const placedCount = document.getElementById('placedCount');
  const overviewPropCount = document.getElementById('overviewPropCount');
  const raycaster = window.THREE ? new THREE.Raycaster() : null;
  let world = window.skyrlWorld;
  let placedGroup;
  let selectedObject = null;
  let selectedType = null;
  let draggedObject = null;
  let draggedPointer = null;
  let movedObject = false;
  let skipNextClick = false;

  function statusMessage(message, isError = false) {
    builderStatus.textContent = message;
    builderStatus.classList.toggle('error', isError);
  }

  function createMaterial(color, options = {}) {
    return new THREE.MeshStandardMaterial({ color, roughness: 0.72, ...options });
  }

  function addMesh(parent, geometry, material, position, rotation) {
    const mesh = new THREE.Mesh(geometry, material);
    mesh.position.set(...position);
    if (rotation) mesh.rotation.set(...rotation);
    parent.add(mesh);
    return mesh;
  }

  function buildObject(item) {
    const group = new THREE.Group();
    group.userData.catalogName = item.name;
    group.userData.yaw = 0;
    const body = createMaterial(item.color, { metalness: item.kind === 'car' ? 0.25 : 0 });
    const dark = createMaterial(0x202632, { roughness: 0.9 });
    const glass = createMaterial(0x526d91, { metalness: 0.25, roughness: 0.25 });
    const green = createMaterial(item.color, { roughness: 0.9 });

    if (item.kind === 'car') {
      const length = item.name === 'Van' ? 4.8 : item.name === 'SUV' ? 4.3 : 4;
      addMesh(group, new THREE.BoxGeometry(1.85, 0.62, length), body, [0, 0.62, 0]);
      addMesh(group, new THREE.BoxGeometry(1.48, 0.65, length * 0.48), glass, [0, 1.18, -0.1]);
      if (item.name === 'Taxi') {
        addMesh(group, new THREE.BoxGeometry(1.2, 0.07, 0.3), createMaterial(0xffe06b), [0, 1.53, 0]);
      }
      if (item.name === 'Police car') {
        addMesh(group, new THREE.BoxGeometry(0.72, 0.12, 0.28), createMaterial(0x5d8dff), [0, 1.57, 0]);
        addMesh(group, new THREE.BoxGeometry(0.4, 0.12, 0.28), createMaterial(0xf25d68), [0, 1.57, 0.3]);
      }
      for (const x of [-0.98, 0.98]) {
        for (const z of [-length * 0.32, length * 0.32]) {
          addMesh(group, new THREE.CylinderGeometry(0.34, 0.34, 0.2, 16), dark, [x, 0.35, z], [0, 0, Math.PI / 2]);
        }
      }
    } else if (item.kind === 'pine') {
      addMesh(group, new THREE.CylinderGeometry(0.22, 0.35, 2.3, 8), createMaterial(0x75533b), [0, 1.15, 0]);
      addMesh(group, new THREE.ConeGeometry(1.55, 3.1, 9), createMaterial(0x286b53), [0, 3.05, 0]);
      addMesh(group, new THREE.ConeGeometry(1.2, 2.7, 9), createMaterial(0x348365), [0, 4.45, 0]);
      addMesh(group, new THREE.ConeGeometry(0.8, 2.2, 9), createMaterial(0x49a17a), [0, 5.65, 0]);
    } else if (item.kind === 'tree') {
      addMesh(group, new THREE.CylinderGeometry(0.3, 0.42, 2.1, 8), createMaterial(0x75533b), [0, 1.05, 0]);
      addMesh(group, new THREE.SphereGeometry(1.7, 14, 12), green, [0, 3.1, 0]);
      addMesh(group, new THREE.SphereGeometry(1.25, 12, 10), createMaterial(0x66ad77), [0.75, 3.25, -0.1]);
    } else if (item.kind === 'bush') {
      addMesh(group, new THREE.SphereGeometry(0.72, 12, 9), green, [0, 0.65, 0]);
      addMesh(group, new THREE.SphereGeometry(0.53, 10, 8), createMaterial(0x65b17c), [0.48, 0.62, 0.12]);
    } else if (item.kind === 'light') {
      addMesh(group, new THREE.CylinderGeometry(0.09, 0.14, 4.7, 8), dark, [0, 2.35, 0]);
      addMesh(group, new THREE.BoxGeometry(1, 0.13, 0.12), dark, [0.43, 4.55, 0]);
      addMesh(group, new THREE.BoxGeometry(0.35, 0.2, 0.35), createMaterial(0xffedb1, { emissive: 0xffce63, emissiveIntensity: 0.8 }), [0.88, 4.43, 0]);
    } else if (item.kind === 'cone') {
      addMesh(group, new THREE.CylinderGeometry(0.04, 0.35, 0.68, 12), createMaterial(0xff783d), [0, 0.36, 0]);
      addMesh(group, new THREE.CylinderGeometry(0.36, 0.36, 0.09, 12), dark, [0, 0.045, 0]);
      addMesh(group, new THREE.CylinderGeometry(0.22, 0.2, 0.09, 12), createMaterial(0xf2eee0), [0, 0.38, 0]);
    } else if (item.kind === 'phone') {
      addMesh(group, new THREE.BoxGeometry(0.38, 0.08, 0.72), dark, [0, 0.12, 0]);
      addMesh(group, new THREE.BoxGeometry(0.32, 0.035, 0.58), createMaterial(0x678fbd), [0, 0.17, 0]);
    } else if (item.kind === 'keys') {
      addMesh(group, new THREE.TorusGeometry(0.22, 0.055, 7, 18), createMaterial(0xd6bd67, { metalness: 0.8 }), [-0.1, 0.14, 0]);
      addMesh(group, new THREE.BoxGeometry(0.12, 0.09, 0.46), createMaterial(0xd6bd67, { metalness: 0.8 }), [0.27, 0.14, 0]);
      addMesh(group, new THREE.BoxGeometry(0.1, 0.08, 0.13), createMaterial(0xd6bd67), [0.34, 0.12, 0.15]);
    } else if (item.kind === 'wallet') {
      addMesh(group, new THREE.BoxGeometry(0.78, 0.22, 0.56), createMaterial(0x815d4d), [0, 0.14, 0]);
      addMesh(group, new THREE.BoxGeometry(0.17, 0.1, 0.58), createMaterial(0xc8a67c), [0.2, 0.28, 0]);
    }

    const ring = new THREE.Mesh(
      new THREE.TorusGeometry(item.kind === 'car' ? 2 : item.kind === 'pine' || item.kind === 'tree' ? 1.4 : 0.8, 0.045, 6, 32),
      new THREE.MeshBasicMaterial({ color: 0x65a7ff, transparent: true, opacity: 0.9 })
    );
    ring.rotation.x = Math.PI / 2;
    ring.position.y = 0.04;
    ring.visible = false;
    ring.userData.selectionRing = true;
    group.add(ring);
    group.userData.selectionRing = ring;
    return group;
  }

  function updateCounts() {
    const count = placedGroup ? placedGroup.children.length : 0;
    placedCount.textContent = `${count} placed`;
    overviewPropCount.textContent = String(count);
  }

  function saveScene(showMessage = true) {
    try {
      const data = {
        version: 1,
        props: placedGroup.children.map(object => ({
          name: object.userData.catalogName,
          x: object.position.x,
          z: object.position.z,
          yaw: object.userData.yaw || 0
        }))
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
      if (showMessage) statusMessage('Environment saved in this browser.');
      return true;
    } catch (error) {
      console.error('Could not save the environment.', error);
      statusMessage('Could not save: browser storage is unavailable.', true);
      return false;
    }
  }

  function selectObject(object) {
    if (selectedObject) selectedObject.userData.selectionRing.visible = false;
    selectedObject = object || null;
    if (selectedObject) {
      selectedObject.userData.selectionRing.visible = true;
      selectedType = selectedObject.userData.catalogName;
    }
    document.querySelectorAll('.object-card').forEach(button => {
      button.classList.toggle('selected', button.dataset.object === selectedType);
    });
  }

  function addObject(name, x, z, yaw = 0, shouldSave = true) {
    const item = itemByName.get(name);
    if (!item || !placedGroup) return null;
    const object = buildObject(item);
    object.position.set(x, world.groundY, z);
    object.rotation.y = THREE.MathUtils.degToRad(yaw);
    object.userData.yaw = yaw;
    placedGroup.add(object);
    if (shouldSave) saveScene(false);
    updateCounts();
    return object;
  }

  function buildPalette() {
    for (const item of catalog) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'object-card';
      button.draggable = true;
      button.dataset.object = item.name;
      button.setAttribute('aria-label', `Drag or select ${item.name}`);
      button.innerHTML = `<span class="object-icon" aria-hidden="true">${item.icon}</span><span><strong>${item.name}</strong><small>${item.size} m</small></span>`;
      button.addEventListener('click', () => {
        selectedType = item.name;
        selectObject(null);
        button.classList.add('selected');
        statusMessage(`${item.name} selected — click the ground or drag it into the scene.`);
      });
      button.addEventListener('dragstart', event => {
        event.dataTransfer.setData('text/plain', item.name);
        event.dataTransfer.effectAllowed = 'copy';
        selectedType = item.name;
        selectObject(null);
      });
      objectList.appendChild(button);
    }
  }

  function groundPoint(clientX, clientY) {
    if (!world || !raycaster) return null;
    const canvas = world.renderer.domElement;
    const rect = canvas.getBoundingClientRect();
    const pointer = new THREE.Vector2(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    );
    raycaster.setFromCamera(pointer, world.camera);
    const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -world.groundY);
    const point = new THREE.Vector3();
    if (!raycaster.ray.intersectPlane(plane, point)) return null;
    point.x = THREE.MathUtils.clamp(Math.round(point.x), -49, 49);
    point.z = THREE.MathUtils.clamp(Math.round(point.z), -49, 49);
    point.y = world.groundY;
    return point;
  }

  function objectAt(clientX, clientY) {
    if (!world || !placedGroup || !raycaster) return null;
    const canvas = world.renderer.domElement;
    const rect = canvas.getBoundingClientRect();
    raycaster.setFromCamera(new THREE.Vector2(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    ), world.camera);
    const hit = raycaster.intersectObjects(placedGroup.children, true).find(entry => !entry.object.userData.selectionRing);
    let object = hit?.object || null;
    while (object && object.parent !== placedGroup) object = object.parent;
    return object?.userData.catalogName ? object : null;
  }

  function placeAt(clientX, clientY, name) {
    const point = groundPoint(clientX, clientY);
    if (!point) {
      statusMessage('That point is outside the ground view. Zoom or orbit the camera and try again.', true);
      return;
    }
    const object = addObject(name, point.x, point.z);
    if (object) {
      selectObject(object);
      statusMessage(`${name} placed at X ${point.x}, Z ${point.z}. Drag to reposition.`);
    }
  }

  function deleteSelected() {
    if (!selectedObject) {
      statusMessage('Select a placed object before deleting.', true);
      return;
    }
    const name = selectedObject.userData.catalogName;
    const object = selectedObject;
    selectObject(null);
    object.parent.remove(object);
    object.traverse(child => {
      if (child.geometry) child.geometry.dispose();
      if (child.material) {
        if (Array.isArray(child.material)) child.material.forEach(material => material.dispose());
        else child.material.dispose();
      }
    });
    updateCounts();
    saveScene(false);
    statusMessage(`${name} removed.`);
  }

  function rotateSelected() {
    if (!selectedObject) {
      statusMessage('Select a placed object before rotating.', true);
      return;
    }
    selectedObject.userData.yaw = ((selectedObject.userData.yaw || 0) + 15) % 360;
    selectedObject.rotation.y = THREE.MathUtils.degToRad(selectedObject.userData.yaw);
    saveScene(false);
    statusMessage(`${selectedObject.userData.catalogName} rotated to ${selectedObject.userData.yaw}°.`);
  }

  function restoreSavedScene() {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (!saved) return;
      const data = JSON.parse(saved);
      if (data.version !== 1 || !Array.isArray(data.props)) throw new Error('Unsupported saved scene format.');
      for (const prop of data.props) {
        if (!itemByName.has(prop.name) || !Number.isFinite(prop.x) || !Number.isFinite(prop.z) || !Number.isFinite(prop.yaw))
          throw new Error('Saved scene contains an invalid object.');
      }
      for (const prop of data.props) addObject(prop.name, prop.x, prop.z, prop.yaw, false);
      updateCounts();
      if (data.props.length) statusMessage(`Restored ${data.props.length} object${data.props.length === 1 ? '' : 's'} from this browser.`);
    } catch (error) {
      console.error('Could not restore the saved environment.', error);
      statusMessage('Could not restore the saved environment. Export or clear the damaged browser save.', true);
    }
  }

  function unityMapData() {
    const walls = [];
    const seen = new Set();
    for (const [x, z, width, depth, height] of world.obstacleLayout) {
      for (let dx = -Math.floor(width / 2); dx < Math.ceil(width / 2); dx++) {
        for (let dz = -Math.floor(depth / 2); dz < Math.ceil(depth / 2); dz++) {
          for (let y = 0; y < height; y++) {
            const cell = {
              x: THREE.MathUtils.clamp(Math.round((x + dx) * UNITY_X_SCALE + UNITY_X_OFFSET), 0, 79),
              y,
              z: THREE.MathUtils.clamp(Math.round((z + dz) * UNITY_Z_SCALE + UNITY_Z_OFFSET), 0, 59)
            };
            const key = `${cell.x},${cell.y},${cell.z}`;
            if (!seen.has(key)) {
              seen.add(key);
              walls.push(cell);
            }
          }
        }
      }
    }
    return {
      start: { x: 10, y: 1, z: 51 },
      walls,
      wins: [{ x: 67, y: 1, z: 49 }],
      invisible: [],
      props: placedGroup.children.map(object => ({
        name: object.userData.catalogName,
        position: {
          x: Math.round(object.position.x * UNITY_X_SCALE + UNITY_X_OFFSET),
          y: world.groundY,
          z: Math.round(object.position.z * UNITY_Z_SCALE + UNITY_Z_OFFSET)
        },
        yaw: object.userData.yaw || 0
      }))
    };
  }

  function downloadUnityMap() {
    if (!world) {
      statusMessage('The 3D scene is not ready; Unity JSON cannot be exported yet.', true);
      return;
    }
    const data = JSON.stringify(unityMapData(), null, 2);
    const url = URL.createObjectURL(new Blob([data], { type: 'application/json' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'drone_map.json';
    link.click();
    URL.revokeObjectURL(url);
    statusMessage('Exported drone_map.json. Copy it to Unity persistentDataPath, then call CellMap.Load() (F9 in Play mode).');
  }

  function importUnityMap(file) {
    const reader = new FileReader();
    reader.addEventListener('load', () => {
      try {
        const data = JSON.parse(String(reader.result));
        if (!Array.isArray(data.props)) throw new Error('This JSON has no Unity props list.');
        const imported = data.props.map(prop => {
          const item = itemByName.get(prop.name);
          const position = prop.position;
          if (!item || !position || !Number.isFinite(position.x) || !Number.isFinite(position.z) || !Number.isFinite(prop.yaw))
            throw new Error(`Invalid or unsupported Unity object: ${prop.name || 'unknown'}.`);
          return {
            name: prop.name,
            x: (position.x - UNITY_X_OFFSET) / UNITY_X_SCALE,
            z: (position.z - UNITY_Z_OFFSET) / UNITY_Z_SCALE,
            yaw: prop.yaw
          };
        });
        clearPlacedObjects(false);
        for (const prop of imported) addObject(prop.name, prop.x, prop.z, prop.yaw, false);
        saveScene(false);
        updateCounts();
        statusMessage(`Imported ${imported.length} Unity object${imported.length === 1 ? '' : 's'}.`);
      } catch (error) {
        console.error('Could not import the Unity scene.', error);
        statusMessage(`Import failed: ${error.message}`, true);
      }
    });
    reader.addEventListener('error', () => statusMessage('Could not read that JSON file.', true));
    reader.readAsText(file);
  }

  function clearPlacedObjects(ask = true) {
    if (ask && placedGroup.children.length && !confirm('Remove every object placed in this environment?')) return;
    for (const object of [...placedGroup.children]) {
      object.parent.remove(object);
      object.traverse(child => {
        if (child.geometry) child.geometry.dispose();
        if (child.material) {
          if (Array.isArray(child.material)) child.material.forEach(material => material.dispose());
          else child.material.dispose();
        }
      });
    }
    selectObject(null);
    updateCounts();
    saveScene(false);
    statusMessage('Placed objects cleared.');
  }

  buildPalette();
  if (!world) {
    statusMessage('The 3D environment did not initialize. Check the connection and reload.', true);
    return;
  }
  placedGroup = new THREE.Group();
  placedGroup.name = 'User placed Unity props';
  world.scene.add(placedGroup);
  restoreSavedScene();

  const canvas = world.renderer.domElement;
  canvas.addEventListener('pointerdown', event => {
    const object = objectAt(event.clientX, event.clientY);
    if (!object) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    selectObject(object);
    draggedObject = object;
    draggedPointer = { x: event.clientX, y: event.clientY };
    movedObject = false;
    canvas.setPointerCapture(event.pointerId);
  }, true);
  canvas.addEventListener('pointermove', event => {
    if (!draggedObject) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const delta = Math.hypot(event.clientX - draggedPointer.x, event.clientY - draggedPointer.y);
    if (delta > 3) movedObject = true;
    if (!movedObject) return;
    const point = groundPoint(event.clientX, event.clientY);
    if (point) draggedObject.position.copy(point);
  }, true);
  canvas.addEventListener('pointerup', event => {
    if (!draggedObject) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const name = draggedObject.userData.catalogName;
    const object = draggedObject;
    draggedObject = null;
    draggedPointer = null;
    if (movedObject) {
      saveScene(false);
      statusMessage(`${name} moved to X ${object.position.x}, Z ${object.position.z}.`);
    }
    skipNextClick = true;
    setTimeout(() => { skipNextClick = false; }, 0);
  }, true);
  canvas.addEventListener('pointercancel', () => {
    draggedObject = null;
    draggedPointer = null;
  }, true);
  canvas.addEventListener('click', event => {
    if (skipNextClick) {
      skipNextClick = false;
      return;
    }
    if (selectedType && !objectAt(event.clientX, event.clientY)) placeAt(event.clientX, event.clientY, selectedType);
  }, true);
  mount.addEventListener('dragover', event => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    mount.classList.add('drag-active');
  });
  mount.addEventListener('dragleave', event => {
    if (!mount.contains(event.relatedTarget)) mount.classList.remove('drag-active');
  });
  mount.addEventListener('drop', event => {
    event.preventDefault();
    mount.classList.remove('drag-active');
    const name = event.dataTransfer.getData('text/plain');
    if (itemByName.has(name)) placeAt(event.clientX, event.clientY, name);
  });

  document.getElementById('rotateObject').addEventListener('click', rotateSelected);
  document.getElementById('deleteObject').addEventListener('click', deleteSelected);
  document.getElementById('saveEnvironment').addEventListener('click', () => saveScene());
  document.getElementById('exportEnvironment').addEventListener('click', downloadUnityMap);
  document.getElementById('clearObjects').addEventListener('click', () => clearPlacedObjects());
  const importFile = document.getElementById('importFile');
  document.getElementById('importEnvironment').addEventListener('click', () => importFile.click());
  importFile.addEventListener('change', () => {
    if (importFile.files[0]) importUnityMap(importFile.files[0]);
    importFile.value = '';
  });
  document.addEventListener('keydown', event => {
    if ((event.key === 'Delete' || event.key === 'Backspace') && selectedObject) {
      event.preventDefault();
      deleteSelected();
    }
    if ((event.key === 'r' || event.key === 'R') && selectedObject) rotateSelected();
  });
})();
