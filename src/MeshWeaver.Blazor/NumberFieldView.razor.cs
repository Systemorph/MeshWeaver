namespace MeshWeaver.Blazor
{
    public partial class NumberFieldView<TValue>
        where TValue:new()
    {
        private TValue Number { get; set; }
        protected override void BindData()
        {
            base.BindData();
            DataBind(ViewModel.Data, x => x.Number);
        }

    }
}
