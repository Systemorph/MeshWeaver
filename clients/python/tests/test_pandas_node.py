"""The pandas mesh node: a live in-process DataFrame controlled over the mesh, rendered as a real
DataGridControl. Driven by a recording connection (no gRPC) — the same duck-typing test_worker uses.

These are the "it's live, not static" proofs: a mutation over the mesh changes the held frame, and the
render surface emits the exact DataGridControl wire JSON the C# GUI renderer consumes."""
import pandas as pd
import pytest

from meshweaver.examples.pandas_node import (
    COMMAND_TYPE,
    GRID_TYPE,
    PandasNode,
    RecordingConnection,
    frame_to_datagrid,
    run_demo,
    sample_sales_frame,
)
from meshweaver.envelope import Delivery


def _submit_code(code: str, activity: str = "ACME/_Activity/1") -> Delivery:
    return Delivery(
        id="c1", sender="@code/ACME/Source/1", target="py/pandas", request_id=None,
        message_type="SubmitCodeRequest",
        message={"$type": "SubmitCodeRequest", "code": code, "activityLogPath": activity}, raw={},
    )


# ---- frame_to_datagrid: the DataGridControl wire contract -----------------------------------------

def test_datagrid_columns_are_typed_property_columns_with_formats():
    grid = frame_to_datagrid(sample_sales_frame())
    by_prop = {c["property"]: c for c in grid["columns"]}
    # String columns -> PropertyColumnControl`1[String], no format.
    assert by_prop["month"]["$type"] == "PropertyColumnControl`1[String]"
    assert "format" not in by_prop["region"]
    # Numeric columns -> typed generic + a .NET numeric format string.
    assert by_prop["sales"]["$type"] == "PropertyColumnControl`1[Double]"
    assert by_prop["sales"]["format"] == "N2"
    assert by_prop["units"]["$type"] == "PropertyColumnControl`1[Int64]"
    assert by_prop["units"]["format"] == "N0"
    # Titles are humanised from the column name.
    assert by_prop["units"]["title"] == "Units"


def test_datagrid_data_are_json_safe_records():
    grid = frame_to_datagrid(sample_sales_frame())
    assert len(grid["data"]) == 6
    first = grid["data"][0]
    assert first == {"month": "Jan", "region": "EMEA", "sales": 120.0, "units": 12}
    # NaN must serialise to null (JSON-safe), not a float nan.
    view = sample_sales_frame()
    view.loc[0, "sales"] = float("nan")
    assert frame_to_datagrid(view)["data"][0]["sales"] is None


async def test_bool_and_datetime_dtypes_map_to_typed_columns():
    df = pd.DataFrame({"active": [True, False], "day": pd.to_datetime(["2024-01-01", "2024-02-01"])})
    by_prop = {c["property"]: c for c in frame_to_datagrid(df)["columns"]}
    assert by_prop["active"]["$type"] == "PropertyColumnControl`1[Boolean]"
    assert by_prop["day"]["$type"] == "PropertyColumnControl`1[DateTime]"
    assert by_prop["day"]["format"] == "yyyy-MM-dd"


# ---- the live object: mutation over the mesh changes real state -----------------------------------

async def test_load_then_append_mutates_the_held_frame():
    conn = RecordingConnection()
    node = PandasNode(conn)
    await conn.send_command(node, "load", data=[{"month": "Jan", "sales": 100.0}])
    assert len(node.dataframe) == 1
    mtype, ack = await conn.send_command(node, "append", rows=[{"month": "Feb", "sales": 110.0}])
    assert mtype == "PandasAck"
    assert ack["rowCount"] == 2                 # the real object grew
    assert list(node.dataframe["month"]) == ["Jan", "Feb"]


async def test_render_returns_a_datagrid_control_of_current_state():
    conn = RecordingConnection()
    node = PandasNode(conn, sample_sales_frame())
    mtype, grid = await conn.send_command(node, "render")
    assert mtype == GRID_TYPE                   # responds AS a DataGridControl (mesh-registered UiControl)
    assert len(grid["data"]) == 6
    assert {c["property"] for c in grid["columns"]} == {"month", "region", "sales", "units"}


async def test_groupby_is_a_real_pandas_aggregation():
    conn = RecordingConnection()
    node = PandasNode(conn, sample_sales_frame())
    _, grid = await conn.send_command(node, "groupby", by="region", agg="sum")
    sums = {row["region"]: row["sales"] for row in grid["data"]}
    assert sums["EMEA"] == 120.0 + 135.5 + 128.0
    assert sums["APAC"] == 98.0 + 143.0 + 150.0


async def test_rolling_mean_adds_a_computed_column():
    conn = RecordingConnection()
    node = PandasNode(conn, sample_sales_frame())
    _, grid = await conn.send_command(node, "rolling", column="sales", window=3)
    col = [row["sales_rolling_mean"] for row in grid["data"]]
    assert col[0] is None and col[1] is None          # first (window-1) rows are NaN -> null
    # to_json truncates doubles to 10 dp — real wire characteristic, so compare with tolerance.
    assert col[2] == pytest.approx((120.0 + 135.5 + 128.0) / 3)
    # rolling is a view: it did NOT mutate the held frame.
    assert "sales_rolling_mean" not in node.dataframe.columns


async def test_unknown_command_replies_error_never_raises():
    conn = RecordingConnection()
    node = PandasNode(conn)
    mtype, payload = await conn.send_command(node, "explode")
    assert mtype == "PandasError"
    assert "unknown command" in payload["error"]


# ---- the code surface: the frame persists across SubmitCodeRequests -------------------------------

async def test_submit_code_persists_frame_state_across_calls():
    conn = RecordingConnection()
    node = PandasNode(conn, sample_sales_frame())
    # First submission adds a derived column to the HELD frame.
    await node.handle(_submit_code("df['margin'] = df['sales'] - df['units']"))
    assert "margin" in node.dataframe.columns
    # A LATER submission sees that column — proof the namespace/frame persists (worker's does not).
    await node.handle(_submit_code("df['margin'].sum()"))
    _, resp = conn.responses[-1]
    assert resp["status"] == "Succeeded"
    expected = float((sample_sales_frame()["sales"] - sample_sales_frame()["units"]).sum())
    assert float(resp["returnValue"]) == expected


async def test_submit_code_writes_back_to_activity_node():
    conn = RecordingConnection()
    node = PandasNode(conn, sample_sales_frame())
    await node.handle(_submit_code("print('rows:', len(df))\nlen(df)"))
    target, mtype, message = conn.posts[0]
    assert target == "ACME/_Activity/1"
    assert mtype == "PatchDataRequest"
    content = message["change"]["content"]
    assert content["status"] == "Succeeded"
    assert content["returnValue"] == 6
    assert any("rows: 6" in m["message"] for m in content["messages"])


# ---- the scripted end-to-end demo -----------------------------------------------------------------

async def test_run_demo_is_live_end_to_end():
    result = await run_demo()
    assert result["started_rows"] == 6
    assert result["row_count"] == 8                     # appended two rows over the mesh
    assert len(result["grid"]["data"]) == 8             # the rendered grid reflects the mutation
    # groupby totals include the appended rows.
    sums = {r["region"]: r["sales"] for r in result["grouped"]["data"]}
    assert sums["APAC"] == 98.0 + 143.0 + 150.0 + 161.0
    assert sums["EMEA"] == 120.0 + 135.5 + 128.0 + 152.0
