using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Shared;
using Xunit;

namespace AssetsManager.Tests.xUnit.Infrastructure
{
    public class PaginationModelTests
    {
        [Fact]
        public void PaginationModel_InitialLoad_StartsOnPageOne()
        {
            var model = new PaginationModel<int>();
            var items = Enumerable.Range(1, 25).ToList();

            model.SetFullList(items);

            Assert.Equal(1, model.CurrentPage);
            Assert.Equal(5, model.TotalPages);
            Assert.Equal("1 / 5", model.PageInfo);
            Assert.Equal(5, model.PagedItems.Count);
            Assert.Equal(1, model.PagedItems[0]);
        }

        [Fact]
        public void PaginationModel_SetFullList_PreservesActivePageByDefault()
        {
            var model = new PaginationModel<int>();
            var items = Enumerable.Range(1, 25).ToList();
            model.SetFullList(items);

            model.CurrentPage = 3;
            Assert.Equal(3, model.CurrentPage);
            Assert.Equal("3 / 5", model.PageInfo);

            var updatedItems = Enumerable.Range(1, 25).ToList();
            model.SetFullList(updatedItems);

            Assert.Equal(3, model.CurrentPage);
            Assert.Equal(5, model.TotalPages);
            Assert.Equal("3 / 5", model.PageInfo);
            Assert.Equal(5, model.PagedItems.Count);
            Assert.Equal(11, model.PagedItems[0]);
        }

        [Fact]
        public void PaginationModel_SetFullList_ClampsWhenListShrinks()
        {
            var model = new PaginationModel<int>();
            var items = Enumerable.Range(1, 25).ToList();
            model.SetFullList(items);

            model.CurrentPage = 5;
            Assert.Equal(5, model.CurrentPage);

            var shrunkItems = Enumerable.Range(1, 8).ToList();
            model.SetFullList(shrunkItems);

            Assert.Equal(2, model.CurrentPage);
            Assert.Equal(2, model.TotalPages);
            Assert.Equal("2 / 2", model.PageInfo);
            Assert.Equal(3, model.PagedItems.Count);
        }

        [Fact]
        public void PaginationModel_SetFullList_ResetsToPageOneWhenExplicitlyRequested()
        {
            var model = new PaginationModel<int>();
            var items = Enumerable.Range(1, 25).ToList();
            model.SetFullList(items);

            model.CurrentPage = 4;
            Assert.Equal(4, model.CurrentPage);

            model.SetFullList(items, preservePage: false);

            Assert.Equal(1, model.CurrentPage);
            Assert.Equal("1 / 5", model.PageInfo);
        }

        [Fact]
        public void PaginationModel_EmptyList_SetsZeroPages()
        {
            var model = new PaginationModel<string>();

            model.SetFullList(new List<string>());

            Assert.Equal(0, model.TotalPages);
            Assert.Equal(0, model.CurrentPage);
            Assert.Equal("0 / 0", model.PageInfo);
            Assert.Empty(model.PagedItems);
        }
    }
}
